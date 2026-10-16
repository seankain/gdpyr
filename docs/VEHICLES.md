# gdpyr — Vehicles that carry units: the technical, and a dropship

**Status: in progress.** V1 (the engine-free cargo rules) and V2 (the technical carries units, on
the server) are built; V2's engine-side checks are outstanding, and V3–V7 are not built. It replaces
the "Dropships and troop carriers" scope cut in [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md)
§5 with a design and a milestone list (M10 there).

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
| V-D3 | **The gesture is a right-click** on a friendly vehicle with units selected. A click that boards anybody **selects the vehicle**, and the vehicle **waits for its passengers**: an order given to it while they are still walking to it is carried out once they are aboard (§5.2). | Left-click replaces the selection (`StrategistController.FinishSelection`), so "select units, then click the vehicle" can only be a right-click. Selecting the vehicle makes "click the truck, click where to go" work; without it the boarders drop out of the selection one by one and the next right-click moves the barracks rally point instead. |
| V-D4 | **More selected than free seats:** the ones nearest the vehicle board (ties to the lower id); **the rest stop where they are.** A vehicle with **no** free seat changes nothing — the client does not send the click and says the vehicle is full. | Deterministic, and an order every selected unit visibly answered; a misclick on a full truck must not stop a squad mid-fight. |
| V-D5 | **Unload is "unload here":** `U` or the card's button. The technical stops; the dropship lands where it is; everybody gets out at once. | The request. "Unload at a point" is one armed key on top of this and is deferred (§14). |
| V-D6 | **Passengers are inert.** They do not shoot, see (fog), capture, heal or throw, and nothing can hit them. The technical keeps its own gun. | One rule, one predicate (§5.1). Firing from the bed is deferred (§14). |
| V-D7 | **A vehicle destroyed with passengers:** a technical, or a dropship within 4 m of the ground, throws them out round the wreck, each taking half its maximum health as explosive damage; a dropship higher than that kills them all. The killer is credited with every unit that dies. | A loaded technical is worth a launcher round, but a squad is not one hit point. 4 m is `CapturePad.VerticalToleranceMeters`, the game's existing "on the ground". |
| V-D8 | **The dropship:** unarmed and order-only (it never picks a target, so Attack and Defend mean "fly there"), 400 health, bullets do half, 12 m/s at 14 m, lands whenever it has nowhere to go, trained at the barracks on `T` for 250 points and 14 s. | Every number is a playtest knob (§8.1). Unarmed keeps it a transport, not a gunship. |
| V-D9 | **Computer strategists never board or unload of their own accord.** They are made robust to passengers instead. | Teaching the commander transports is new goals, a squad-tree change and a clash with the technical's scout role (§9). |
| V-D10 | **Agent API: additive only.** New commands, `list_units` fields and events; schema stays `gdpyr-agent-obs-2`, action heads unchanged. | Every trained checkpoint keeps loading (§10). |
| V-D11 | **Wire:** the 13-byte unit record stays 13 bytes. One flag bit says *aboard*, two say the flight phase, one says *cannot land*, and an aboard unit's yaw field carries its vehicle's id. `DemoFormat.Version` 1 → 2. | §6. |
| V-D12 | **The ground force can work out how many ride in each vehicle** from the unit snapshot. Accepted. | Units are one unfiltered broadcast by design (`NETCODE.md` §6.2); a modified client can already count the army. |

---

## 3. The shape of it

A passenger **stays a `Unit`** — same id, same registry entry, same health. It is alive, hidden,
solid to nothing, not simulated, and pinned to its vehicle's position every tick. Nothing is
despawned and nothing is respawned. That is what keeps `LiveUnitCount` (the defeat condition),
`MaxUnits`, demo playback (where the snapshot is the roster, `UnitManager.ApplyRecordedSnapshot`)
and every agent observation row correct without a second representation.

The alternative — despawn into a manifest of `(definition, health)` and respawn on unload — was
rejected: it loses ids, plans, squads and grenades, turns every unload into new `unit_built`-shaped
traffic, makes demo playback despawn and respawn on every boarding, and needs the defeat condition
to count something that is not a unit.

```
right-click vehicle ─► RequestEmbark(ids, vehicle) ─► ApplyEmbark: seats, nearest first, rest stop;
                       (selection becomes the vehicle)   the vehicle holds (a dropship lands) and waits
                                                          │
             each tick: walk to a point beside the vehicle (Order.Kind = Embark)
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
- **`Cargo.Admit(candidates, distances, freeSeats, boarders, leftovers)`** — takes distinct ids
  (the caller de-duplicates, as `ApplyOrder` does: a client chooses the list), orders them by flat
  distance to the vehicle, ties to the lower id, and splits them at `freeSeats` (V-D4).
- **`Cargo.InReach(vehicle, vehicleRadius, unit, unitRadius)`** — flat (XZ) distance ≤
  `vehicleRadius + unitRadius + Cargo.ReachSlackMeters` (1 m), and the unit's feet within
  `CapturePad.VerticalToleranceMeters` (4 m) of the vehicle's: 2.4 m for a rifleman at the
  technical, 3.4 m at the dropship. Flat, so a slope does not matter; banded, so a rifleman on a
  roof above a parked truck does not board it and later get out at street level.
- **`Cargo.StandOff(vehicle, unit, vehicleRadius, unitRadius, heightAboveGround = 0)`** — where a
  boarder walks to: the nearest point to it on a ring `vehicleRadius + unitRadius + 0.5 m` round the
  vehicle, inside reach — at ground height (`heightAboveGround` below the vehicle) for a vehicle off
  the ground, so a walker can arrive while a dropship is still coming down (`InReach`'s band, measured against the vehicle, still keeps it from boarding
  until it lands). Never the vehicle's centre: an airborne dropship is solid to nothing, and
  boarders standing under its centre are the bodies its landing checks wait for (§8.2).
- **`Cargo.InField(point)`** — |x|, |z| ≤ 115 m: the one bound for exit slots, landing spots and
  aircraft destinations. The ground outside the walls is a navmesh island of its own (the hidden
  300 × 300 m slab is in the `navmesh` group), so no navmesh test keeps anything off it.
- **`Cargo.ExitSlot(vehicle, yaw, vehicleRadius, passengerRadius, ordinal)`** — the `ordinal`-th
  candidate point: a row behind the vehicle first, then its sides, then its front, then a second
  ring further out, each `vehicleRadius + passengerRadius + 0.6 m` from the centre and 1.2 m apart
  (`StructureTraining.Slot`'s spacing). `Cargo.ExitSlotCount` says how many there are before they
  wrap: 26 for a rifleman round the technical (10 + 16), 36 round the dropship (15 + 21). Candidates
  only: the engine half checks each one (§5.4).
- **`Cargo.BoardTimeoutTicks`** (20 s) — how long a boarder walks before it gives up.
- **`Cargo.WreckDamage(maxHealth)`** and **`Cargo.WreckKillsAll(heightAboveGround)`** — V-D7's two
  numbers, so the rule is one place.

Tests: the fifth rider is refused at four seats; a duplicate is refused; `Admit` keeps nearest
first and returns the rest; reach is flat within the band and refused above it; the stand-off point
is in reach and outside the vehicle's radius at every approach angle; exit slots are clear of the
vehicle's radius and of each other; the first slot is behind the vehicle at every yaw; the wreck
rule's threshold is inclusive at 4 m.

### 4.2 `Scripts/Sim/Flight.cs` (new, V6) — `Tests/FlightTests.cs`

The dropship's phase machine:

```
            wants air                    reached cruise
 Grounded ───────────► TakingOff ──────────────────────► Cruising
    ▲                      ▲ wants air                       │ nowhere to go, and a landing spot found
    │ touched down         │                                 ▼
    └──────────────────────┴──────────────────────────── Landing
```

- `FlightTraits` (from `UnitDefinition.ToFlightTraits()`): cruise altitude 14 m, climb 4 m/s,
  descent 3 m/s, **clear altitude** 10 m — below it the dropship moves only vertically, so it never
  slides into a barracks roof (6.5 m) or a sniper tower cabin (8.4 m) on the way up or down.
- `Flight.Step(traits, ref state, wantsAir, heightAboveGround, dt)` returns the vertical velocity
  and whether horizontal movement is allowed. "Wants air" is "has a destination further than its
  arrival radius, measured flat". `FlightState` also carries `CannotLand`, set while it holds
  cruise for want of a landing spot.
- `Flight.CanBoard` / `CanUnload` are true only when `Grounded`.

Tests: take-off reaches cruise in `ceil(14 / 4 / dt)` ticks; no horizontal movement below clear
altitude; landing returns to the ground; a new destination during landing takes off again;
boarding and unloading are refused unless grounded; altitude never leaves
`±SimConfig.MaxPositionMeters` (327.67 m; the map's floor top is at 0.5 m).

---

## 5. The server

### 5.1 Unit state, one predicate, one invariant

On `Unit` (`Scripts/Rts/Unit.cs`):

- `ushort RidingIn` — the vehicle's `UnitId`, 0 on foot. `bool IsRiding => RidingIn != 0`.
- `bool IsOnField => IsAlive && !IsRiding` — **the question every system outside the unit loop
  is really asking when it says `IsAlive` today.**
- On a unit with seats: `CargoHold Cargo`; `int CargoIssuer` (the peer whose embark request last
  reserved or filled a seat; §9.1); `UnitOrder Departure` and `bool HasDeparture` (an order that
  waits for boarders, §5.2).
- On an aircraft: `FlightState Flight` (§8).

**Invariant: a dead unit is never aboard.** Every path that kills a rider clears `RidingIn` and
removes it from the hold first (§5.5); `UnitManager.Damage` clears them if it ever kills one that is
still set; `BroadcastUnitSnapshot` never encodes a record that is both dead and aboard.

**Presentation in one place.** `SetDeadPresentation` (`Unit.cs:296`) becomes `ApplyPresentation()`:
`Visible = IsAlive && !IsRiding`, `CollisionLayer = Units` only then (0 otherwise), and on the
server `CollisionMask = 0` while riding so the hidden body never pushes its vehicle. An aircraft off
the ground also gets `CollisionLayer = 0` (§8.3). It is called from `ApplyDamage`, on boarding and
unloading on the server, and from `ApplyRemoteState` on a puppet's first record and on any change
of alive, aboard or flight phase (today it reacts only to alive, `Unit.cs:280`). Two setters would
undo each other — a passenger killed with its vehicle shown again, or a corpse shown on unload.

### 5.2 Orders and requests

`OrderKind` (`Scripts/Sim/UnitOrder.cs`) gains two wire values, appended:

| Value | On | `Target` | `Anchor` | `TargetOwnerId` |
|---|---|---|---|---|
| `Embark = 7` | a passenger walking to its vehicle, and while it rides | walking: `Cargo.StandOff` beside the vehicle, refreshed every tick; riding: the vehicle's position, written by the pin (§5.3) | where it stood when ordered, as `ApplyOrder` sets it — the unit HTN's `Encode` reads it | `OwnerId.ForUnit(vehicle)` |
| `Unload = 8` | a vehicle | where it came to rest (recorded only) | the same | — |

Neither goes through the generic order message: `UnitOrder.IsIssuable` stays the range
`Move..Stop`, which already excludes 7 and 8, as it excludes `Build`. `UnitBrain.Destination`
gains `Embark => order.Target`; `Unload` falls into its default case, *no destination*, so
`Unit.Steer` zeroes the vehicle's horizontal velocity on the first tick (a destination, even the
vehicle's own position, would keep it driving at an old path corner inside `SetDestination`'s
0.75 m re-path threshold). Neither holds to fight (`HoldsWhileEngaging` stays false), so a unit
walking to board shoots on the way as on a move.

They arrive by their own requests, in a new partial **`Scripts/Rts/UnitManager.Cargo.cs`**, each
the `Request*`/`Client*`/`Server*`/`Apply*` quartet `RequestAssist` uses
(`UnitManager.Structures.cs:186`) so a person, a bot and an agent reach the same checks.

**Rejections.** One rule for these two requests and for `ApplyOrder` (`UnitManager.cs:1649`),
written into `NETCODE.md` §6.3 with them: a malformed id, a unit or vehicle of the wrong team, an
ineligible kind of unit and a sender who is not a strategist count in `RejectedOrders`; per-id
state that can change within a round trip — dead, riding, already gone — is skipped without
counting. An honest client's selection lags the server by a round trip, so a unit that boarded, or
died, a moment ago is a race, not a forged packet. Today `ApplyOrder` counts a dead id, which makes
"0 for honest clients" untrue after any death; this changes that too.

- **`ClientEmbark(int[] unitIds, int vehicleId)`** → `ApplyEmbark`. Refused unless the vehicle is a
  live unit of the sender's with seats that is not itself riding. Candidates are the distinct ids
  that are live own units that can ride (V-D1) and are not the vehicle. **Free seats** are the
  vehicle's seats, less those aboard, less boarders already walking to it *that are not in this
  request* — so clicking the same vehicle twice with the same selection keeps them walking. No free
  seat: nothing changes for anybody. Otherwise `Cargo.Admit` picks the boarders; they get `Embark`,
  the rest `UnitOrder.Hold(where they stand)`; all of them get issuer and tick, `NextScanTick = 0`,
  `ResetPlan`, `Suppression.Clear`, `Squads.Leave`. **The vehicle** is set to hold where it is (for
  a dropship, land there; §8.2), with the sender as issuer and as `CargoIssuer`; a standing `Unload`
  is cancelled. From here no computer strategist orders it (§9.1).
- **`ClientUnload(int[] vehicleIds)`** → `ApplyUnload`. Same sender and ownership checks; an empty
  vehicle is a no-op (the button is disabled, but a passenger may have died). Sets the vehicle's
  order to `Unload`, issuer and tick, `ResetPlan`; boarders still walking to it stand down to
  `Hold`, and a waiting departure is dropped.
- **The vehicle waits for its boarders.** `ApplyOrder` given a vehicle that has boarders walking to
  it stores the order as its `Departure` instead of applying it; `ServerCargo` applies it once
  nobody is walking to it (all aboard, re-ordered, dead or timed out). `Stop` applies at once and
  drops the departure.
- **Riders are skipped by `ApplyOrder`** under the rule above. Ordering a vehicle never touches its
  passengers.

### 5.3 The tick

In `SimulateUnit` (`UnitManager.cs:441`), right after the `IsAlive` check and before anything else
(`PlanOf`, the scan, `AcquireTarget`):

1. **A rider returns.** No plan, scan, fire, throw, steer or resupply.
2. **A unit whose order is `Embark`** resolves it with `ResolveEmbarkOrder`, as `ResolveBuildOrder`
   resolves a builder's: the vehicle must be alive, same team, with a free seat or this unit's
   reservation, and the order younger than `Cargo.BoardTimeoutTicks`, or the unit stands down to
   `Hold`. In reach of a vehicle that can take it (`Flight.CanBoard` for the dropship) it **boards
   and returns**: `RidingIn` set, `Cargo.TryAdd`, `Velocity = 0`, `ApplyPresentation`, `ResetPlan`,
   `Squads.Leave`, `HasCoverSpot = false`, `Unit.TargetOwnerId = None` (the shooting target — not
   `Order.TargetOwnerId`, which names the vehicle), `Suppression.Clear`, and a `unit_embarked`
   event. Returning before the scan means a boarding unit spends no sight-line ray and cannot fire
   or throw from inside on its last tick. Otherwise its `Order.Target` becomes the stand-off point
   and it carries on as a unit on a move. Registry order decides who takes the last seat when two
   arrive on one tick.

A new pass **`ServerCargo(tick)`** runs in `ServerTick` (`UnitManager.cs:255`) after
`ServerStructures` — whose `TryComplete` can still move a vehicle out of a finishing footprint — and
before `ServerReapCorpses` and `BroadcastUnitSnapshot`. Both server entry points
(`PlayerManager.ServerTick` and `OfflineTick`) reach it through `UnitManager.ServerTick`.

- **Departures:** a vehicle with a waiting `Departure` and nobody walking to it gets it.
- **Unload:** a vehicle whose order is `Unload` and that is at rest — the technical the tick after
  the order, the dropship once `Grounded` — lets everybody out (§5.4). When its hold is empty it
  holds where it is.
- **Orphans:** a vehicle with passengers or boarders whose `CargoIssuer` is no longer seated as a
  strategist — on the strategist team and either a person or a bot seat with a policy attached
  (`AgentServer.IsAttached`) — unloads where it stands, and its boarders stand down. On the server,
  whatever AI is running or none, so a person who loads a squad and leaves cannot strand units that
  never fight and, being alive, never let the strategist lose.
- **Pin, last:** every rider's `GlobalPosition`, and its `Order.Target`, become its vehicle's
  position. Position only — nothing reads a rider's velocity, and a copied one would survive into
  its first step after it gets out.
  Fog, snapshots, the agent views and the spectator then read a rider at the right place with no
  code of their own.

### 5.4 Getting out

For each rider in hold order, take `Cargo.ExitSlot` candidates in turn and accept the first that
passes five checks, as shared helpers beside `ValidateSite` (`UnitManager.Structures.cs`):

- **in the field** — `Cargo.InField`;
- **reachable** — new: a World-layer ray from the vehicle's centre 1 m up to the slot at the same
  height. Without it a technical parked against a perimeter wall puts its second ring through the
  1 m wall onto the strip outside, which is clear, on ground and on navmesh; the same goes for a
  sandbag or barracks wall;
- **ground** — a World-layer ray down from the reachability line's height (1 m over the slot)
  rather than `ValidateSite`'s from 6 m above, so a slot under a roof finds the floor and not the
  roof; and the ground within `CapturePad.VerticalToleranceMeters` of the vehicle's feet, the band a
  unit gets in from, so nobody gets out at the foot of a drop;
- **navmesh** — `NavigationServer3D.MapGetClosestPoint` within a tolerance of the passenger's own
  (its radius + 0.3 m flat, 0.5 m vertical), not `ValidateSite`'s, which is sized for a structure
  (half-extent + 1.5 m); skipped, as `ValidateSite` skips it, while `NavigationReady` is false;
- **clearance** — new: an `IntersectShape` with the passenger's capsule lifted 0.15 m against
  World, Units and Players, excluding the vehicle. `Obstructs` tests a box against World only. The
  navmesh check alone is not enough either: the map's invisible `Cube`, `Cube2` and `Cube3`
  colliders are not carved from it.

The helpers keep one ray query, one shape query and one capsule per definition and mutate them in
place; only the result containers Godot hands back are allocated, as the sight-line rays' are
today. Slots taken this tick are remembered in a scratch array, since a body moved this tick may
not be in the broadphase yet.

A placed rider: `GlobalPosition` = the slot, `Velocity = 0`, `RidingIn = 0`, `Cargo.Remove`,
`ApplyPresentation`, `Order = Hold(slot)` from the unloading peer with the current tick (so D1's
claim follows the unloader), `NextScanTick = 0`, a forced re-path (the agent's 0.75 m threshold
would otherwise keep the pre-boarding path, `Unit.SetDestination`), and a `unit_disembarked` event.
It is in no squad until its next order, as a fresh unit at its door is. A rider with no clear slot
stays aboard and is retried every scan interval (10 ticks) while the order stands; a new order to
the vehicle cancels the unload and leaves the rest aboard, and the card still counts them.

### 5.5 Wrecks

The rider rule needs the killer and the weapon, which `UnitManager.Damage` does not have, so the
wreck is recorded one level up, in **`CombatManager.DamageUnit`** (`CombatManager.cs:983`, the only
caller of `UnitManager.Damage`): when it kills a vehicle with anybody aboard it appends
`{vehicle, attackerOwnerId, weapon, heightAboveGround}` to a fixed list and does nothing else.

**`CombatManager.ResolveWrecks(tick)`** runs in `ServerPostTick` right after `StepProjectiles` and
before `Economy`, `ServerRound` and `Visibility` — after every kill of the tick (a melee kill in
`SimulatePlayers` earlier the same tick included) and outside every damage loop. For each rider of
each wreck:

1. `RidingIn = 0`, `Cargo.Remove`, `Velocity = 0`.
2. The wreck low enough (`Cargo.WreckKillsAll` false): placed as in §5.4 (with a `unit_disembarked`
   event), shown, and dealt `Cargo.WreckDamage` as explosive damage. Otherwise, or with no clear
   slot: put at the wreck's position projected to the ground, shown, and dealt the rest of its
   health.
3. The damage goes through the private `DamageUnit` with `attacker = Find(OwnerId.PeerOf(id))` when
   the stored id is a peer still on the roster, else null, and the stored weapon — so `Damage`,
   `Kill` and `unit_lost` events are the normal ones and name the killer. A killer who has left, or
   was not a player, gets no `Kills` and no marker. `ConfirmHit` fires per rider; no hit message is
   broadcast for them.

Resolving after the projectile step is what makes it order-independent. Riders are invisible to
`QuerySegment` and `Explode` (§5.6), so the blast that killed the vehicle cannot touch them, and the
unit splash loop walks the registry by index (`CombatManager.cs:878`), so resolving inside it would
hurt the riders listed after the vehicle and spare those before. The snapshot after it, and
everything in the rest of `ServerPostTick`, see a consistent state. The corpse is reaped two
seconds later (`ServerReapCorpses`), long after its riders are out.

`Despawn` and `ClearUnits` run on the server, on live clients and in demo playback. On the server
only, `Despawn` asserts that a vehicle it frees has nobody aboard and removes a freed rider from its
vehicle — never a placement, never an event. `ClearUnits` clears every hold and the wreck list
before it despawns anything. A puppet's `RidingIn` is only ever what its newest record says.

### 5.6 Every consumer of "is this unit in the world"

Each of these filters on `IsAlive` (or nothing) today and switches to `IsOnField`, or to the rule
given:

| Where | What changes |
|---|---|
| `UnitManager.QuerySegment` (`UnitManager.cs:1429`) | skips riders: rounds and melee cannot hit them |
| `CombatManager.Explode`, unit splash loop (`CombatManager.cs:878`, filter at `:885`) | skips riders |
| `VisibilityService.BuildSensors` (`VisibilityService.cs:270`) | riders are not sensors — they would also crowd the three-candidate `VisionField.Gather` list at the vehicle's point and starve a sensor elsewhere that could see |
| `EconomyService` presence count (`EconomyService.cs:230`) | riders do not hold a node; the vehicle counts as one body |
| `UnitManager.CollectSupplies` | a riding truck is not a source (cannot happen under V-D1; filtered anyway) |
| `UnitManager.CountSquads` (`UnitManager.cs:896`) | riders and aircraft are neither neighbours nor census members: otherwise they skew `Neighbourhood` odds, `ControlPoint.Holds` and squad centroids — a dropship's 14 m-high position and its over-the-wall view most of all |
| `UnitManager.ApplyOrder`'s `Squads.Form` batch | leaves aircraft out, so a dropship in a mixed order adds no strength to the squad |
| `UnitManager.Cover` (`UnitManager.Cover.cs:110`, `:179`) | boarding clears `HasCoverSpot`, so both loops agree |
| `UnitManager.TryComplete` (`UnitManager.Structures.cs:635`) | does not teleport riders, or an aircraft passing over, out of a finishing footprint |
| `UnitManager.GatherBuilders` (`UnitManager.Structures.cs:486`) | skips riders — `PlaceSite` would charge for a site nobody can walk to |
| `UnitManager.BuilderCount` (`UnitManager.Structures.cs:137`) | counts builders on the field, for the HUD's "builders N" |
| `UnitManager.DescribeUnitPlans` (`UnitManager.cs:1357`) | skips riders |
| `UnitManager.ApplyOrder` | skips riders (§5.2) |
| `UnitManager.LiveUnitCount` | **unchanged**: riders are the strategist's army for `WinConditions` |
| `GroundSensor.Scan` (`GroundSensor.cs:129`) | skips riders: no bot sees or targets them |
| `BotDirector.PruneGroundContacts` (`BotDirector.cs:274`) | forgets a rider as it forgets a dead unit, or the shared contact memory keeps it for 8 s |
| `BotPilot.TryTargetState`, `TryCoverThreat` (`BotPilot.cs:540`, `:888`) | a rider is a lost target and no threat |
| `BotStrategist` (§9.1) | `Decide`, `FillZones` and legacy `Command` leave riders out; `Fortify` and `QueueSupply` count them but never order them |
| `StrategistController.IsSelectable`, `PruneSelection` (`:1008`, `:1191`) | riders are not selectable and drop out of a selection |
| `SpectatorView` pick (`SpectatorView.cs:472`), `AiEntities.TryPosition` | riders are not pickable; following one follows its vehicle |
| `AiDebugPublisher` unit labels (`AddUnitLabels`) | no label for a rider. A label has no field for an aboard count (`AiLabel`), so a vehicle's aboard and seats are inspect values (§10), not label text |
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
| **4–5** | **flight phase** — 0 grounded, 1 taking off, 2 cruising, 3 landing; always 0 on ground units |
| **6** | **cannot land** — an aircraft holding cruise for want of a landing spot (the card says so) |
| 7 | spare |

`UnitSnapshot` gains `ushort RidingIn` (aboard is `RidingIn != 0`), `FlightPhase Phase` and
`bool CannotLand`. **While aboard, the u16 yaw field carries `RidingIn`** — a hidden passenger's
heading means nothing — and the position is the vehicle's. `Encode` writes
`aboard ? RidingIn : YawToU16(Yaw)`; `TryDecode` sets `RidingIn` and `Yaw = 0` for an aboard record,
and reads an aboard record naming id 0 as on foot, so it stays total. At both client push sites
(`ApplyPendingSnapshots`, `ApplyRecordedSnapshot`) an aboard record pushes the puppet's own current
yaw, never the id read as an angle.

The client counts each vehicle's passengers from the records that name it. Units are never
fog-filtered or truncated, so the count is exact — as of the newest snapshot applied: today every
pending packet is applied in arrival order, and an unreliable packet arriving late would re-show a
boarded rider or flip a landed dropship's collision for one interval. `UnitManager` keeps the newest
applied unit-snapshot tick, as `PlayerManager` does for players; every packet still feeds the
interpolators, but state, health and the new bits are applied only from a packet at or after it.
Recorded playback is in order and unaffected.

**Getting in and out happens at the render clock.** State is decoded ~100 ms before the
interpolated transform reaches the same tick (`InterpolationDelayTicks = 6`), so a rider shown at
decode would stand inside the truck bed, then slide out through the hull. A puppet stores the tick
of its aboard change; `ClientTick` applies the presentation once the render tick passes it, and on
getting out the interpolator is reset, so the unit appears at its slot.

`MaxPayloadBytes` stays 837 for a full field of 64. Riders stay **in** the packet — leaving them
out would make demo playback despawn them (`UnitManager.ApplyRecordedSnapshot`) and freeze live
puppets, visible, where they boarded.

The rejected layout appends a u16 vehicle id (15 B, 965 B for 64): simpler to decode, two bytes
per unit per snapshot for what is zero on almost every record.

### 6.2 Everything else

- **`DemoFormat.Version` 1 → 2**, as its own comment requires for any change to a copied codec;
  old demos are refused by `DemoHeader.IsPlayable`. `ApplyRecordedSnapshot` handles the new bits as
  the live path does.
- **Joining peers:** `ServerSpawnUnit` carries no aboard bit. A new puppet is hidden and off its
  layer — set after `AddChild`, since `_Ready` sets `CollisionLayer = Units` — until its first
  record, on which `ApplyRemoteState` applies its presentation whatever it says (§5.1); so a
  passenger never flashes visible to a joiner, and every other unit appears on its first record.
- **New messages** (`NETCODE.md` §7 table): `ClientEmbark` ~8 B + 4 B per unit and `ClientUnload`
  ~4 B + 4 B per vehicle, both reliable, client → server. No new server → client message: the
  snapshot carries it all.
- **One build for everybody.** There is no live-wire version check, and new `[Rpc]` methods on
  `UnitManager` probably renumber its RPC ids (not verified here). A server and clients from
  different commits misroute silently instead of failing a decode. This is the existing practice;
  it is now load-bearing.

---

## 7. The strategist's controls

| Input | Does |
|---|---|
| right-click a friendly technical or dropship, with units selected | the units that can ride board it (V-D3, V-D4); the vehicle becomes the selection |
| `U`, or **Unload (U)** on the card | every selected vehicle with somebody aboard unloads where it is |
| `T` | queue a dropship at the barracks |

`U` (physical keycode 85) and `T` (84) are free; every digit is taken and `G` must stay unique
(`GrenadeTests.TheGrenadeKeyIsBound`).

- **`StrategistController.CommandAtCursor`** (`:643`) keeps an armed placement (`5`/`6`/`7`) first:
  with a structure armed, the right-click needs a ground point and places it, as today. Otherwise
  it calls `TryEmbarkAtCursor()` before the `TryGroundPoint` early return, since embarking needs
  only the cursor and the vehicle's screen position (a dropship against the sky has no ground point
  under it). It fires only when: the pending order is Move (an armed `X`/`C`/`B` means a ground
  order — and `ArmPlacement` resets the pending order to Move, which is why placement is tested
  separately); the selection holds at
  least one unit that can ride other than the vehicle; and the nearest own vehicle with seats is
  inside a pick radius scaled by its on-screen size and nearer the cursor than any enemy contact
  and — with builders selected — any structure `TryAssistAtCursor` would pick. `PickUnit`'s fixed
  36 px round `GlobalPosition + Up` would let a rifleman beside the truck steal the click, and miss
  the top of a 4 m dropship. If the client's aboard count says the vehicle is full, nothing is sent:
  a "full" marker flashes and the selection line says so. Otherwise it sends `RequestEmbark` with
  the eligible ids, flashes an embark marker on the vehicle, **replaces the selection with the
  vehicle** and resets the pending order to Move. A selection of vehicles only falls through.
- **`FinishSelection`**'s single click uses the same size-scaled vehicle pick before `PickUnit`, so
  "select it again" lands on the truck and not on a rifleman just out of it.
- **`_UnhandledInput`** gets `rts_unload` and `rts_build_dropship` in its chain.
- **`StrategistHud`**, a third card mode. `Refresh` is passed a summary of the selection (vehicles,
  aboard, seats, cannot-land) rather than `_selected.Count`. With vehicles selected, `RefreshCard`
  hides the progress bar, every unit button and Cancel, and shows the status
  ("technical · 3/4 aboard", "2 vehicles · 5/8 aboard", "dropship · no room to land") and a new
  **Unload (U)** button made with `NewCardButton`, disabled when nobody is aboard. The footer hint,
  a local today, becomes a field set per mode ("right-click with units selected to board"). A new
  `event Action UnloadPressed` is wired in `StrategistController._Ready` beside `BuildPressed`, to
  the same handler as `U`. `CardUnits`/`CardKeys` gain the dropship and `T`; the tooltip gains
  "seats 8". The key-help label gains a line, and its fixed 168 px height and the card's offsets
  grow with it.
- **`SelectionOverlay`:** seat pips above every own vehicle with seats (filled for aboard), drawn
  like `DrawStructures`' bars; a ground ring under an airborne dropship; colours for the embark,
  full and unload markers in `OrderColor`.
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
| health, bullets do | 220, all | 400, half (`BulletDamageScale = 0.5`, the first fractional value on a unit; the sandbag wall's 0.3 already runs the path) |
| `IsVehicle`, grenades | yes, 0 | yes, 0 |
| speed | 8 m/s | 12 m/s in the air, 0 on the ground |
| body | radius 1.0, height 2.2 | radius 2.0, height 4.0 (height ≥ 2 × radius keeps the hitbox a capsule) |
| sees, shoots | 70 m, 50 m | 40 m, nothing |
| seats | 4 (new) | 8 |
| trained by, key | barracks, `2` | barracks, `T` |

**Unarmed means order-only.** `WeaponId` is clamped into the catalog, so every unit has a gun, and
`EngageRangeMeters = 0` alone stops it firing but not acquiring: `AcquireTarget` picks by sensor
radius, and `UnitBrain.Destination` sends an Attack at its target and lets a Defend chase inside
its leash. A dropship would fly to the nearest player and, arriving flat, land beside them. So
`SimulateUnit` does not call `AcquireTarget` for a unit whose engage range is zero: it never has a
target, Attack goes to its point, Defend to its anchor, and an Attack naming a player is a flight
to where the click was.

New `UnitDefinition` exports: `Seats` (0 carries nobody) in "Body"; a "Flight" category —
`CruiseAltitudeMeters` (0 = a ground unit; `IsAircraft` is `> 0`), `ClimbMetersPerSecond`,
`DescentMetersPerSecond`, `ClearAltitudeMeters` — with `ToFlightTraits()` beside `ToTraits()`.
`UnitCatalog` gains `SeatsOf`, `CanRide` and `IsAircraft`, after `CanConstruct`/`IsSupply`.

### 8.2 Flying

- **`Unit.Steer`** (`Unit.cs:174`) branches for aircraft into `SteerFlying`: no
  `NavigationAgent3D` (`BuildAgent` is skipped for aircraft), a straight line in XZ at `MoveSpeed`
  when `Flight.Step` allows horizontal movement, vertical velocity from `Flight.Step`, `Velocity`
  always written with Y (ground bots lead with it), then `MoveAndSlide` with
  `MotionMode.Floating` while airborne. Height above ground is one World-layer ray down per tick,
  only while it is off the ground, through the same cached query as §5.4.
- **`SimulateUnit` measures arrival flat for aircraft only** (`UnitManager.cs:515`, `:521`): a
  dropship 14 m above its destination is there. Ground units keep the 3D distance — legacy's
  decisions must not move (D5).
- **It lands whenever it has nowhere to go:** on arriving from a Move, Attack or Defend; on Stop,
  `Hold` and `Unload`, where it is. Patrol keeps it flying between its ends. An embark request
  sets it to hold, so a dropship in the air — patrolling or passing — comes down where it is to be
  boarded; boarders walk to the stand-off ring (§4.1), not under it.
- **Landing spot:** on entering `Landing`, `TryLandingSpot(target)` runs §5.4's checks (bar
  reachability) with a hull-sized shape from the ground to clear altitude, over a short
  nearest-first ring of candidates (6 m, then 12 m out), each inside `Cargo.InField`. Boarders whose
  `Embark` names this dropship are not obstacles to it. None clear: it holds cruise with
  `CannotLand` set, and the card says "no room to land". At 2 m it re-checks for other bodies
  (Units, Players) beneath and waits there until clear.
- **Destinations are bounded where the aircraft steers**, not where orders arrive: `SteerFlying`
  clamps its destination into `Cargo.InField` and replaces a non-finite one with its own position.
  Orders reach an aircraft by more paths than `ApplyOrder` — `ServerBuild` writes a fresh unit's
  Move from the barracks' rally point, which `ApplyRally` stores unchecked, and `ServerPlaceUnit`
  writes its own — and none of them checks either.
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
non-builder into a `CommandUnit`; once a person's 30 s claim (D1) lapses, it would re-order riders
and boarders, recruit a loaded or loading technical as its scout (`Commander.Recruit` takes
technicals first) and patrol it away with the person's squad aboard, and put a dropship in the
garrison. So:

- **`Decide` and `FillZones` leave out riders, boarders, vehicles with passengers or boarders, and
  aircraft**; `ApplyIntents` skips them as a guard. They are left out unconditionally, from the
  click on, not through `IsClaimed`: `IsClaimed` returns false at once for a unit whose issuer is
  the bot's own peer (`BotStrategist.cs:283`), and a policy's seat is a bot peer whose commander
  covers for it whenever the policy goes quiet — a claim test would hand a policy's loaded
  technical to its own fallback commander, which recruits technicals first. Nothing is lost: by
  V-D9 no computer strategist loads anything, and §5.3's orphan rule unloads what a departed loader
  left. Emptied, a vehicle is an ordinary unit again under D1.
- Aircraft are left out for their own reasons: no role, never in the garrison, and in `FillZones`
  a landed, unarmed dropship at the rally point would add 250 points of "friendly strength" to the
  barracks zone that `Commander.Fallback` retreats squads to. Never bought (not in `Tiers`;
  `BotStrategist.Build` treats tier index as definition id, so adding it there would make the
  builder buyable).
- **`Fortify` and `QueueSupply` count riders but never order them.** Both count builders and
  trucks against `LiveUnitCount`, which includes riders; skipping a riding builder in the count
  would make the bot buy a second one. Riders are skipped only when choosing a unit to order
  (`_idleBuilders`, and the `ServerAssist`/`ServerConstruct` targets) — a riding builder's order is
  `Embark`, not `Build`, so it would otherwise count as idle.
- The technical keeps `Scout`. A loading or loaded one is never offered to `Recruit` because it is
  never in the commander's input. `CommanderHtnTests`' recon facts and `htn_recon_finds_contact`
  must pass unchanged.
- Legacy `Command` (`:565`) re-orders any unit whose order kind differs from what it wants, with no
  claim at all. It skips riders, boarders, vehicles with passengers or boarders, and aircraft. For
  a round with no vehicles carrying anybody that is no change, which is what D5 requires.

### 9.2 Unit HTN

- Riders are never planned: `SimulateUnit` returns before `PlanOf`, and boarding calls `ResetPlan`.
- `UnitDomain`'s Retreat and Resupply sequences exclude `Move` and `Build` orders twice each — as
  preconditions (`IfNot`, `UnitDomain.cs:958–959`, `:972–973`) and as executing conditions
  (`WhileNot`, `:962`, `:976`), since every operator has an executing condition for its premise.
  `Embark` and `Unload` join both, so an autonomous unit walking to board, or a vehicle unloading,
  is not pulled off it.
- **A last selector, `carry`, obeys `Embark` and `Unload`**: `IfAny(Order, Embark, Unload)`, then
  `UnitGoal.Obey`, executing while the order stands. Every stored order kind has a top-level
  selector today; one that matched none would fall back to `UnitBrain.Destination` correctly, but
  FluidHTN replans whenever it has no task, so every boarder and every unloading vehicle would run
  a failing `FindPlan` each tick. `Obey` is `FollowOrder`, which takes "stop to fight" from
  `HoldsWhileEngaging`, so behaviour is the same. No new fact or goal: `HtnSchema` takes the order
  fact's values from `OrderKind`. `UnitHtnTests` gains Embark and Unload rows (a person's boarder
  never retreats or resupplies; an autonomous unloading vehicle never retreats) and the
  zero-allocation run gains both kinds.
- The domain's signature changes; the `HtnDebugTests` signature facts compare builds with each
  other, so they hold, and a spectator from an older build is refused as it would be for any
  domain change. In a round with no vehicle carrying anybody, no new condition is ever false, so
  no decision changes.
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
  `unit_disembarked` {unit, vehicle, tier, x, z} (getting out of a wreck included),
  `dropship_landed` {unit, aboard, "", x, z}. `tools/Gdpyr.Playtest/EpisodeMetrics` mirrors them;
  `AgentEventTests`' `Last` assertion moves.
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
- **AI debugger:** `AiValueKey` appends `RidingIn`, `Aboard`, `Seats`, `Altitude` as inspect
  values; `AiMarkerKey` appends `Vehicle` (where a boarding unit is walking); `AiDebugText`'s unit
  header prints "riding in technical 12" or "aboard 3/4" from them; `AiDebugWorld` labels the new
  marker, and may draw "aboard n/m" over a vehicle from the snapshot's count (§6.1). Byte enums are
  appended and no frame field is added, so `AiDebugCodec.Version` stays 1.
- **`tools/gdpyr_env`** needs no code change: it passes command dicts through and reads
  `list_units` and events by name.

---

## 11. Harness and scenarios

The harness cannot express this yet: a spawn cannot refer to another spawn, `Runner.Act`
(`tools/Gdpyr.Playtest/Runner.cs:246`) ignores strategist seats, and `ScriptStep` is ground input
only.

- **Spawns** gain an optional `"id"` label and `"aboard": "<label>"` naming an earlier unit spawn.
  This is a new kind of parse-time check: `Scenario.ReadSpawns` validates only the one-of rule
  today, and a seat named by `peer` is resolved at run time in `Runner.SpawnAsync`. A forward or
  unknown label is a `ScenarioException` with a sentence. The server boards the unit directly
  (harness-only, like `ServerPlaceUnit`), with the scenario's strategist seat — named by the
  spawn's `peer`, or the only one — as the vehicle's `CargoIssuer` and the riders' issuer. A
  harness-placed unit has no issuer, and §5.3's orphan rule would unload a vehicle loaded by
  nobody on its first tick; so a scenario with an `aboard` spawn and no strategist seat is refused
  at parse time. A stepped seat never falls back to its bot, so it stays seated.
- **Strategist scripts:** a step type of timed commands — `embark`, `unload`, `order` — naming units
  by spawn label; `Runner` keeps spawned ids by label and sends the steps through
  `GdpyrConnection.ActCommands`.
- Parser facts for both in `Tests/ScenarioTests.cs`.

New scenarios. An assertion is one metric against one value, so each claim below is a single
metric:

| Scenario | Set-up | Asserts |
|---|---|---|
| `technical_boards_squad` | a technical and six riflemen beside it; scripted embark of all six | `unit_embarked.count == 4` |
| `technical_carries_squad` | a technical with four aboard; scripted move 60 m, then unload | `unit_disembarked[x>…].count == 4`; `unit_lost.count == 0` |
| `technical_wreck_ejects` | a technical with four aboard on open ground, ~55 m from a scripted grenadier on `scripts/locker_then_fire.json` (`locker_launcher_vs_tank`'s pattern, since a scenario cannot arm a bot) — outside the technical's 50 m engage range, inside its 70 m sensor, so it cannot kill the grenadier first; the spawn's `look` pitch retuned for the range; a stepped strategist seat for the `aboard` spawn | in every seed: `unit_lost[tier=1].count == 1`, `unit_disembarked.count == 4`, `observation.grenadier.self.alive.min == 1` (a 40 mm round does 130 of the technical's 220) |
| `dropship_lands_to_unload` | a dropship with eight aboard; scripted move, then unload | `dropship_landed.count >= 1`; `unit_disembarked.count == 8` |

**The baseline.** The last measured suite is 18 of 19 at `fcf2a5d`, with
`htn_strategist_defends_node` failing seed 2 (`HTN_BOTS.md`, the combined-assault section).
`cee3f28` since changed the unit domain's Attack selector — the cause that section gives for that
failure — and recorded no suite result. So V2 starts by running `./scripts/playtest.sh` over
`Tests/Scenarios/*.json` at its own start commit, and that run is the baseline V5 compares against.

---

## 12. Milestones

Each ends with `dotnet test` green, the tests named here added, and the docs it touches updated in
the same change.

### V1 — Cargo rules (½ day) — done

`Scripts/Sim/Cargo.cs`, `SimConfig.MaxPassengers`, `Tests/CargoTests.cs` (§4.1).

- **Result:** `dotnet test` 1,602 passed, 0 failed (1,572 before; 30 new in `CargoTests`), no
  warnings in the new files; `dotnet build Gdpyr.csproj` 0 warnings, 0 errors. The guards were
  checked to fail: an exclusive 4 m wreck threshold, ties to the higher id, the first exit slot in
  front, and a reach with no height band each fail one or two facts. Beyond §4.1: `ReachMeters`,
  `StandOffMeters`, `ExitRingMeters` and `ExitSlotCount` are public so V2 and the tests share the
  numbers; `CargoHold`'s ordinal accessor reads 0 past the last rider; `Admit` counts a missing or
  NaN distance as furthest and never drops a candidate for want of room in its boarders span;
  `WreckKillsAll` treats an unknown (NaN) height as high. Nothing in the game calls any of it yet.

### V2 — The technical carries units, on the server (2 days) — built; the engine-side checks are outstanding

- Record the scenario baseline (§11).
- `UnitDefinition.Seats`; `Units/technical.tres` `Seats = 4`; `UnitCatalog.SeatsOf`/`CanRide`.
- `OrderKind.Embark`/`Unload`, `UnitBrain.Destination`'s `Embark` case (`UnitBrainTests`: Embark
  goes to its target; Unload has no destination).
- `Unit.RidingIn`, `IsOnField`, `Cargo`, `CargoIssuer`, `Departure`, `ApplyPresentation`.
- `UnitManager.Cargo.cs`: the two request quartets, the rejection rule (with `ApplyOrder`'s),
  `ResolveEmbarkOrder`, `ServerCargo` (departures, unload, orphans, pin), the ground, navmesh and
  clearance helpers; `CombatManager`'s wreck list and `ResolveWrecks`.
- §5.6's sweep, in full, in this milestone — a half-done sweep leaks hidden units into the game.
- **Done when:** on a listen host, units board, ride, unload and survive a wreck; a passenger
  cannot be hit, does not count on a node and does not light the fog; "click the truck, click
  where to go" drives it away full.
- **Result:** `dotnet test` 1,609 passed, 0 failed (1,602 before; 7 new: 6 in `UnitBrainTests` —
  Embark goes to its target, Unload has no destination, neither is issuable or stops to fight, both
  are appended — and 1 in `TrainedUnitResourceTests`, the technical's four seats and nobody else's);
  `dotnet build Gdpyr.csproj` 0 warnings, 0 errors. Built: `UnitDefinition.Seats`,
  `technical.tres` `Seats = 4`, `UnitCatalog.SeatsOf`/`CanRide`; `OrderKind.Embark = 7`/`Unload = 8`;
  `Unit.RidingIn`/`IsRiding`/`IsOnField`, `Cargo`, `CargoIssuer`, `Departure`, `ApplyPresentation`
  (replacing `SetDeadPresentation`), `ForceRepath`; `Scripts/Rts/UnitManager.Cargo.cs` — both
  request quartets, `SimulateCargo`/`ResolveEmbarkOrder`/`Board`, `ServerCargo` (orphans,
  departures, unload with a 10-tick retry, the pin), the exit-slot checks and `EjectFromWreck`;
  `ApplyOrder`'s rejection rule and departures; `CombatManager`'s wreck list and `ResolveWrecks`;
  `Despawn`/`ClearUnits`' hold rules; §5.6's sweep in full, and the review grep for `IsAlive` over
  `Scripts/`, whose remaining unit uses are the army counts (`LiveUnitCount`, `Build`'s census,
  `Fortify`'s and `QueueSupply`'s counts), the damage entry points, the cover claims (boarding
  clears the spot), and the two agent views that keep riders.
- **Not checked, and why:** there is no Godot in the environment this was built in, so the scenario
  baseline (§11) was not recorded and the "done when" above was not run. Nor can it be run on V2
  alone: nothing a person or a policy can press reaches `RequestEmbark` or `RequestUnload` until
  V3's right-click and `U`, or V4's agent commands. The baseline is therefore V3's or V4's first step.
- **Changed from the plan above, in the code and here:** the exit slot's ground ray (§5.4, above).
  §9.1's exclusion of boarders and of vehicles with passengers or boarders from `Decide`,
  `FillZones`, `ApplyIntents` and legacy `Command` (`BotStrategist.IsInTransport`) is brought
  forward from V4: without it a computer co-strategist recruits a person's loaded technical once the
  30 s claim lapses. An embark keeps an order the vehicle was already keeping; the vehicle leaves
  its squad on an embark or an unload, and a departure puts it in a squad of its own, as the order
  would have. A vehicle whose loader has gone unloads on nobody's word (issuer 0), so no claim
  holds its riders. A rider thrown from a wreck holds on its own last issuer's word. An unload of a
  vehicle with nobody aboard does nothing, boarders walking to it included. The debug HUD's
  "(n riding)" appears only when n > 0.
- **Left for V4, with their kinds:** the `unit_embarked`, `unit_disembarked` and `dropship_landed`
  events — `Board`, `GetOut` and the dropship's landing are where they go; and §9.2's `carry`
  selector, without which an HTN unit walking to board, or a vehicle unloading, runs a failing
  `FindPlan` each tick and moves by `UnitBrain.Destination`, which is the right behaviour at a
  planning cost.

### V3 — The wire and the strategist's controls (1½ days)

- `UnitFlags` bits and the yaw-slot rule, `UnitSnapshot`'s fields, `UnitSnapshotCodec`, client
  apply on both paths with the newest-tick guard, hidden-until-first-record puppets, getting in and
  out at the render clock, the per-vehicle aboard count; `DemoFormat.Version = 2`.
- §7: right-click, selection, `U`, the card's third mode, the overlay, the input map, `README.md`.
- Tests: `UnitSnapshotCodecTests` (aboard and vehicle id round trip; an aboard record naming 0
  decodes on foot; flight and cannot-land bits; a ground record decodes as before; state, team and
  the new bits share the byte; still 13 B and under one datagram), `DemoCodecTests` (version).
- **Done when:** the same on a dedicated server with a remote strategist and a remote ground player,
  and a remote client sees a freshly built unit appear.

### V4 — Bots, agents, debugger (1 day)

§9 and §10. `AgentEventTests`, `StrategistActionSpaceTests` (a rider is in no group),
`AgentObservationTests` (order bytes 7–8 and tier 7 leave the one-hots zero), `UnitHtnTests`'
Embark/Unload rows and zero-allocation run, and the existing `CommanderHtnTests` unchanged. On a
listen host with a computer co-strategist: a riding builder does not make it buy another, and a
quiet policy's loaded technical is not recruited by its fallback commander.

### V5 — Harness and the technical's scenarios (1 day)

§11's spawn labels, `aboard`, strategist scripts, parser facts, and the three technical scenarios.
The existing suite re-run against V2's baseline.

### V6 — The dropship (2–3 days)

`Scripts/Sim/Flight.cs` and `FlightTests`; `Units/dropship.tres`, id 7, the flight exports;
`SteerFlying`, no acquisition when unarmed, flat arrival, landing spots, clamped targets, spawning;
collision by phase; the ground bots' goal projection; presentation; `T` and the card;
`dropship_lands_to_unload`. `TrainedUnitResourceTests` (path 7 is the dropship; only the technical
and the dropship have seats; only the dropship has flight keys),
`GrenadeTests.UnitsOnFootCarryThree_VehiclesNone` (the dropship joins its vehicle list).

### V7 — Docs (alongside each)

A `NETCODE.md` §10.10 in §10.7's shape (the rule, the table, on the wire, who uses it, not in it);
§6.1's record description, §6.3's checks and rejection rule, §7's table, §10.6 (its "everything
else the strategist fields is soft" and the armour table gain the dropship at 0.5) and §10.9 (the
dropship joins both lists of vehicles that carry no grenades); `DEMOS.md`'s version
note; `AGENT_API.md` §6.2, §7.4, §7.5 (`list_units`' fields and example), §8, §9.1; `HTN_BOTS.md` on
the claim for loading vehicles; `TRAINING.md` on multi-unit loss; this document's status, and
`IMPLEMENTATION_PLAN.md` §5's scope-cut row struck as the builders' was.

**About 8–9 days in all**; V1–V5 (the technical) stand on their own and ship first.

---

## 13. Risks

| Risk | Signal | Mitigation |
|---|---|---|
| A consumer still asks `IsAlive` and leaks a hidden passenger into play | bots shoot at a parked truck's empty bed; a node is held by nobody visible; selection grabs invisible units | One predicate; §5.6 done in one milestone; a review grep for `IsAlive` over `Scripts/` before V2 closes |
| Passengers stranded aboard | live units that never fight; a round that cannot end | Unload retries; the server-side orphan rule; riders show in the debug HUD's units line |
| Boarders that never arrive | a truck that waits for ever; seats reserved by units across the map | The vehicle waits only while somebody is walking to it, and boarders give up after 20 s |
| Unloading into geometry | units stuck in a wall, or put through one onto the strip outside the map (which has navmesh of its own) | Slots inside `Cargo.InField`, in sight of the vehicle, and checked against the navmesh with a passenger's tolerance *and* physics; slots taken this tick remembered |
| Dropship lands on a body, or cannot land | units or players squeezed out; a dropship hovering for ever | Pre-landing and 2 m checks; boarders stand off; "no room to land" from the wire's own bit |
| Ground bots helpless against the dropship | `htn_*` ground scenarios degrade when a strategist fields one | Half bullet damage, not none; goal projection; drop compensation in §14 if playtests ask |
| Mixed client and server builds after new RPCs | every unit RPC misrouted, silently | Ship together (§6.2); a join-time protocol version is a separate, small change worth making |
| Recon regresses because the technical is now also a transport | `htn_recon_finds_contact` fails | The commander never loads a technical; a loading or loaded one is never in its input; the recon tests gate V4 |
| Reward or metric spikes from multi-unit deaths | `StrategistReward` and `unit_lost` counts jump when a loaded vehicle dies | It is what happened; `TRAINING.md` says so (V7) |

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
