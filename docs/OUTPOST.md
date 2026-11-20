# The outpost, the scout drone and the operative

A builder's fourth structure, and the two units it trains. What was asked for:

> The outpost can create 2 new units, the scout drone and the operative. The scout drone is a
> hovering drone that can patrol areas and reveal ground unit positions. It has a one-time attack of
> exploding that must be initiated by the strategist explicitly. The operative is a humanoid unit
> with low armor and health but high speed and aggressive tactics. Moving from cover to cover and
> using smoke grenades for open spaces the operative tries to get close for suppressed pistol or
> knife kills on the ground units. The operative looks for covering/suppressive fire opportunities
> from infantry, tanks and heavy gunners.

"Ground units" are the ground force: the players and ground bots the strategist fights. Every number
below is in a resource file or a rules class and is a playtest knob; §6 is what was checked, and §7
what was not.

## 1. Where it lives

| | |
|---|---|
| `Structures/outpost.tres` | the outpost |
| `Units/scout_drone.tres`, `Units/operative.tres` | the two units |
| `Weapons/knife/`, `Weapons/suppressed_pistol/`, `Weapons/smoke_grenade/`, `Weapons/scout_charge/`, `Weapons/projectiles/{pistol_9mm_subsonic,smoke_grenade,scout_charge}.tres` | what they carry |
| `Scripts/Sim/Smoke.cs` | `SmokeRules`, `SmokeField`: the cloud, and whether it stops a sight line |
| `Scripts/Sim/ScoutDrone.cs` | `StrikeRules`, `StrikeSim`: the strike |
| `Scripts/Sim/Operative.cs` | `OperativeRules`, `OperativeSim` (the tactics), `CoveringFireBoard`, `UnitMelee` |
| `Scripts/Rts/UnitManager.Strike.cs`, `UnitManager.Operative.cs` | the engine halves |
| `Scripts/Match/CombatManager.Outpost.cs` | the charge going off, the knife landing, the smoke let out |
| `Tests/OutpostTests.cs` | the engine-free rules and the files |
| `Tests/Scenarios/{outpost_trains_both,scout_drone_reveals_and_strikes,operative_*}.json` | headless playtests |
| `tools/EngineChecks/OutpostChecks.cs`, `scripts/outpost-checks.sh` | the in-engine check, with screenshots |

Everything engine-free is in `Scripts/Sim` and tested without Godot; the rest follows the code it
sits beside (the sniper tower and the pillbox, the dropship, the hand grenade, the HTN units' cover).

## 2. The outpost

| | |
|---|---|
| cost, build time | 125 points, 12 s for one builder |
| body | 3.2 × 3.2 m, 2.4 m high, 450 health; `BulletDamageScale` 0.3 — rifles chip at it as at sandbags, explosives do it whole |
| eyes | 35 m, from four points outside its walls at 1.7 m, as a pillbox's |
| gun | none |
| trains | the scout drone (`TrainsUnitId` 8) and the operative (`AlsoTrainsUnitId` 9), from one queue |

It is drawn as a squat hut with a band of windows, a dark pad with a pale cross on its roof, and a
4.5 m mast with a dish at one corner (`Structure.BuildOutpost`); only the box is hit or collided with.

**Two kinds, one queue.** `StructureDefinition.AlsoTrainsUnitId` is a second kind; a structure that
names none trains one, as before. A train request now names the kind it wants
(`UnitManager.RequestTrain(slot, unitId)`, the RPC `ClientTrain(int slot, int unitId)`), and
`StructureTraining.TryChoose` decides what it gets: the named kind when the structure trains it; at a
structure that trains one kind, that kind whatever was named — so a tower or a pillbox behaves as it
always did; at an outpost, anything else is refused and counted in `RejectedOrders`. The training
state every client is sent carries the head's kind as well (`ServerTrainingState(slot, queued, head,
progress)`, seven bytes), so the card can say which of the two is training. The door, the rally
point, the refund when it is knocked down and the `unit_built` event (`barracks` = −(slot + 1)) are the
tower's and the pillbox's.

**The strategist.** `O` arms it for the builders' next right-click, as `5` `6` `7` arm theirs; the
builder card has an **Outpost (O)** button. Clicked, its card shows **Scout Drone (-)** and
**Operative (=)**; `-` and `=` queue one at the selected outpost, else at the one with the shortest
queue.

## 3. The scout drone

| | |
|---|---|
| cost, train time | 75 points, 6 s |
| body | a 0.6 m capsule of 0.3 m radius; 60 health, bullets do all of it; a vehicle, so a hand grenade does a quarter and it boards nothing |
| speed | 9 m/s across, 5 up, 4 down |
| flight | cruise 12 m, clear altitude 9 m (above the tower's 8.4 m cabin) |
| eyes | 60 m from 12.3 m up |
| gun | none (`EngageRangeMeters` 0) |
| charge | `Weapons/projectiles/scout_charge.tres`: 170 at the centre falling to nothing at 6 m — a kill within about 2.5 m; half of that to vehicles and structures |

### 3.1 It hovers

`UnitDefinition.Hovers`: an aircraft that wants the air whether or not it has anywhere to go
(`UnitManager.SteerAircraft`). It climbs straight up out of the outpost's door to its clear altitude,
then flies a straight line at its cruise, and with nowhere to go holds its altitude where it is, rather
than looking for somewhere to land as the dropship does. Drawn as a quadcopter — a flat body, four
arms and four rotors that turn while it is up, and a red camera under its nose (`Unit.BuildQuadcopter`).

### 3.2 It reveals and patrols

Nothing new was needed for either. A unit's sensor is the strategist side's eyes
(`VisibilityService`): a ground-force player inside 60 m of the drone with a clear line from it is
visible to the strategist, is sent in the strategist's snapshot, raises `contact_spotted` with
`observer` 8, and goes into the side's contact memory that the HTN units — and the operative — plan
from. From 12 m it looks down over every low wall: a player crouched behind a sandbag wall that a
rifleman in front of it cannot see is seen by a drone hovering over the rifleman (§6.2). Patrol is the
ordinary patrol order (`C`), which an aircraft already flew between its two ends.

### 3.3 It goes off only when the strategist says

`OrderKind.Strike` (9) is an order like attack-move, armed with `J` (or the card's **Strike**, shown
only while a drone is selected) and given by the next right-click: on the ground it is a point, on an
enemy player it names them. It goes through the generic order message, and the server gives it only to
the units of the order that carry a charge (`UnitDefinition.StrikeWeaponId`); a rifleman selected with
the drone keeps the order it had. Nothing else in the game issues it: no planner, reflex or computer
strategist path names `Strike`, and an agent seat or a scenario does only by sending `strike`.

A drone with a strike flies straight at its aim at 18 m/s, floating and against the world only
(`Unit.FlyStrike`). Its aim is a named player's chest while the drone itself can see them — the order's
point follows them, so a player who ducks out of sight is dived on where they went — else the order's
point, 1 m over the ground. It goes off (`StrikeSim`):

- within 1.5 m of its aim (`end` 1);
- when an enemy player's middle comes within 2 m of its own (`end` 2);
- when the move runs into anything (`end` 3);
- after 8 s of flying at it, where it is (`end` 4).

The blast is `CombatManager.Explode` with the charge's round, in the drone's name: every ground-force
player, crew vehicle and the like in reach takes it, and the strategist's own side is spared, as from
every unit's blast. The drone dies with it (`unit_lost`, its own owner id), and `unit_strike` records
why it went off and whether it killed. A later order — a stop, a move — calls a strike off before it
ends, and the drone climbs back to its cruise.

## 4. The operative

| | |
|---|---|
| cost, train time | 80 points, 6 s |
| body | a rifleman's — 1.8 m, crouching to 1.0 m behind a sandbag wall — with 60 health to the rifleman's 100 |
| speed | 7 m/s to the rifleman's 4.5 |
| eyes, reach | 45 m; it engages to 22 m |
| pistol | `suppressed_pistol`: a subsonic 9 mm, 34 a hit (three to a kill), 240 a minute, 12 to a magazine, 72 rounds carried; a 1.2° cone |
| knife | `knife`: an instant swing that reaches 1.8 m from its eye and does 150 — a kill |
| smoke | three smoke grenades a life; no hand grenades |
| doctrine | `UnitDoctrine.Operative` (4) |

### 4.1 Its quarry

With tactics on (`H`, Auto, the default) and an order it can hunt under — none, defend, patrol or
attack; never a move, which is how it is placed — it looks, at its scan rate (six times a second), for:

1. the player its attack order names, while they are alive;
2. else the player it has in its sights;
3. else the nearest ground-force player the side remembers, alive, within its sensor and 15 m more —
   on a defend order, within twice its 25 m leash of what it defends.

Where they are is where it sees them, else where the side last saw them, else where the order said.
With no quarry, or with tactics off, it is a soft fast unit with a pistol that does what it is told,
and its planner has it as it has any unit with a doctrine (cover from fire, returning fire).

### 4.2 Cover to cover (`OperativeSim`)

```
            cover spot ahead                       arrived / too long
  ┌──────────────────────────► Bound ──────────────────────────────┐
  │                                                                ▼
 (new quarry) ── in cover ───────────────────────────────────────► Hold ◄─┐
  │               covered, screened or the way on unwatched, or waited too long │
  │                       ├─ a cover spot ahead ───────────────► Bound    │
  │                       ├─ open ground, watched, smoke in hand ► Smoke ─► Dash
  │                       └─ open ground otherwise ────────────► Dash ────┘
  └── inside 10 m, from anywhere ────────────────────────────────► Close: shoot, stab
```

- **Bound**: to the next cover spot — on its way to the quarry, at least 3 m nearer them, within 16 m
  and 8 m of the line, on its own side of the box, hiding a crouched body from them, and not claimed
  by another of the side's units (`CoverBoard.TryFindSpot`). Running, standing.
- **Hold**: there, crouched, calling for covering fire (§4.5). It goes on after at least half a second
  when its side is firing on its quarry, smoke stands between them, or its quarry would not see it
  standing 3 m on — they look the other way (§4.4), or a wall is in the way — or, watched over open
  ground with smoke in hand, to lay the screen; and after four seconds regardless: it is aggressive,
  not patient.
- **Smoke**: one smoke grenade along the line to its quarry, 9 m out — never past halfway, never nearer
  than 6 m — and it waits two seconds for the cloud to stand. Not when its side is already firing on
  them: then it runs and keeps its smoke.
- **Dash**: across the open towards them, to 9 m short of them, at most two seconds; then down where it
  is and look again.
- **Close**: inside 10 m it runs straight at them, and stabs when its knife reaches — once every 0.8 s.

`operative` events record each phase it goes into, each smoke and each stab (§6.2 has a run of them).

### 4.3 Smoke

A smoke grenade is thrown like a hand grenade, on the low arc, on a two-second fuse, and harmless.
Where it goes off, `CombatManager.LetOutSmoke` puts a cloud on the floor under it into
`CombatManager.Smoke` (`SmokeField`, `SmokeRules`):

| | |
|---|---|
| radius | 1 m at once, 4.5 m after 1.5 s, held, thinning to nothing over the last 3 s |
| life | 18 s |
| middle | 1.2 m over where it went down |
| blocks | a line with at least 1 m of itself inside the sphere |

A line through a cloud is stopped for everybody who asks the server: a unit's target acquisition
(`UnitManager.HasLineOfSight`), a barracks' or a structure's gun (`DefenseBattery`), the ground bots'
eyes (`GroundSensor`) and the strategist's fog (`CombatManager.HasLineOfSight`). Somebody inside a
cloud is hidden from outside it and sees nothing out of it. Rounds pass through it. Every peer is sent
the grenade's hit message as for any round that goes off, and draws the cloud from it to the same
numbers (`ExplosionKind.Smoke`: sixteen puffs as wide as the cloud, let out over the bloom), so what a
person sees and what the server's rays meet are the one cloud.

### 4.4 Pistol and knife

It holds its fire further than 14 m from its quarry — the pistol reaches 22 — and while its quarry
cannot see it: no clear line, or they are looking more than 65° away from it (`OperativeSim.Watches`,
with the quarry's own look while the operative can see them, and supposing they look its way while it
cannot). Shot at, it shoots back regardless. So a quarry watching it is shot once it is close; one
looking away, or behind its smoke, is walked up to and stabbed (`CombatManager.ServerUnitStab`: the
blade's reach measured to their capsule from its eye, nothing solid between, and nobody in a crew
vehicle's seat). The pistol's shot is `ShotKind.SuppressedPistol`: a cough heard to 90 m where a
pistol carries 250, almost no flash; its round still whizzes past an ear.

### 4.5 Covering fire

`CoveringFireBoard`, one per side:

- **Fire it can move under.** Every shot a rifleman, a tank or a heavy gunner fires
  (`UnitDefinition.GivesCoveringFire`) is noted with where it was aimed. A shot aimed within 6 m of an
  operative's quarry in the last 1.5 s is covering fire, and it moves on under it (§4.2).
- **Fire it calls for.** While it holds, smokes, dashes or closes, an operative calls for fire on its
  quarry where it last knew them, renewed each tick and withdrawn the moment it stops — or dies. A rifleman, tank or heavy gunner
  with tactics on, nothing of its own to shoot, rounds left, and an order of none, defend, patrol or
  attack takes up the nearest call whose quarry is in its reach and whose operative is within 70 m of
  it, if the world leaves it a line to the quarry's chest — smoke aside: it fires into smoke — and fires
  at that place, as a unit returning fire at a place does. `covering_fire` records each one that takes
  a call up. A player crouched behind a wall the rifleman cannot see over is still a place to fire at:
  the rounds go over the wall at chest height, and the operative runs.

## 5. On the wire and the API

Every table that is a wire constant was appended to, never reordered:

| | appended |
|---|---|
| `UnitCatalog` | 8 `scout_drone`, 9 `operative`; `SimConfig.MaxUnitDefinitions` 8 → 16 (every table sized by it is per kind; the byte on the wire was always a whole byte) |
| `StructureKinds` / `StructureCatalog` | 3 `outpost` |
| `WeaponCatalog` | 17 knife, 18 suppressed pistol, 19 smoke grenade, 20 scout charge |
| `OrderKind` | 9 `Strike`, issuable |
| `UnitDoctrine` | 4 `Operative` |
| `ShotKind` | 9 `SuppressedPistol` |
| `ExplosionKind` | 6 `Smoke` |
| `AgentEventKind` | 30 `unit_strike`, 31 `operative`, 32 `covering_fire` |

The agent API's `strategist_action` publishes `outpost` and `strike`; a `build` with a `slot` at an
outpost takes `tier` 8 or 9; a scenario spawns `scout_drone`, `operative` and `outpost`, orders
`strike`, and its new `train` command queues a unit at a structure slot
([`AGENT_API.md`](AGENT_API.md)). The observation schemas are unchanged: the strategist vector's tiers
are still the three fighting tiers, and the two new units are in its unit block as units of no tier.

The keys are `O`, `-`, `=` and `J`, none used by anything else on the strategist's side
(`OutpostTests.TheOutpostsKeys_AreBound_ToKeysNothingElseOnTheStrategistsSideUses`); `G` stays the
grenade's alone.

## 6. What was checked

### 6.1 `dotnet test`

3,984 tests pass, 46 more than before the outpost: 37 in `Tests/OutpostTests.cs`, and the rest existing
theories that now run over the new shot kind, rounds and weapons. `OutpostTests` holds the cloud's bloom, life and thinning;
the chord through a sphere; a line through a cloud stopped, one beside it and one grazing its edge not,
someone inside it hidden and blind; a full field giving up its oldest cloud; a strike flying straight
at its aim at its speed, never past it, going off on arrival, near an enemy and when its time runs
out; `Strike` issuable and naming a target; every branch of §4.2's machine — holding crouched in
cover, bounding and holding at the spot, going on only when covered, screened or unwatched, smoking
watched open ground and running after the cloud stands, keeping the smoke under the side's fire,
running anyway after four seconds, a dash ending in a hold, rushing and stabbing at the knife's
interval, holding fire far or unseen and returning it when shot at, a new quarry a new approach;
where smoke is thrown; what a quarry watches; a knife's reach; covering fire noted and answered, by the
nearest in reach and near the caller, replaced, withdrawn and lapsed; which kind a train request gets;
and the files and tables — the outpost, the two units, the four weapons and their numbers, who gives
covering fire, every explosion kind having a pool to be drawn from, the appended events, and the keys.
Existing tests were changed only where they pinned a count or a table the additions lengthen (the
weapon count, the card's key row, the last event kind, the explosive rounds, the flyby list) or the
smoke grenade is the first round to go off without a blast.

### 6.2 Headless scenarios (`./scripts/playtest.sh`, Godot 4.6 .NET, `--bot-ai htn`)

| scenario | seeds | what it shows |
|---|---|---|
| `outpost_trains_both` | 1 | `train` at slot 0 for a scout drone, an operative and a sniper: one drone and one operative come out (`barracks` −1), the sniper is refused, nothing is lost |
| `scout_drone_reveals_and_strikes` | 4, 12 | a player crouched behind a sandbag wall, a rifleman 29 m in front of it that cannot see them, and a drone moved to hover 14 m short of the player: `contact_spotted` by `observer` 8 and never by 0; then `strike` given to the drone and the rifleman — one `unit_strike`, `end` 2 at the wall, `killed` 1, the player dead, the drone lost, the rifleman not |
| `operative_closes_and_kills` | 3, 9 | a player standing in the open 42 m off, looking at the operative, two walls between: bound, hold, bound, hold, smoke, dash, and three pistol hits for the kill |
| `operative_knifes_from_behind` | 3, 9 | the same player looking away, one wall: crouched behind it the operative cannot see which way they look, supposes they are watching and smokes the ground ahead; then dash, hold, dash, close — no shot fired — and one stab of 150 for the kill |
| `operative_covering_fire` | 3, 9 | the player crouched behind a wall that neither the operative nor a rifleman beside it can see over, and a drone that can: the drone spots them, the operative hunts them from the side's memory, calls for fire from behind its wall, the rifleman takes the call up (`covering_fire`, `tier` 0) and fires over their wall, and the operative dashes under it without smoke, closes and knifes them |

Each passed on every seed. `operative_closes_and_kills`, seed 3, as its trace has it (`phase`: 1
bound, 2 hold, 3 smoke, 4 dash; `act` 1 a smoke thrown):

```
operative  phase 1 act 0  at (-60.0,  8.0)     out from where it was put
operative  phase 2 act 0  at (-60.0, 20.0)     behind the first wall
operative  phase 1 act 0  at (-60.0, 20.0)
operative  phase 2 act 0  at (-59.1, 31.1)     behind the second
operative  phase 3 act 0  at (-59.1, 31.1)
operative  phase 3 act 1  at (-59.1, 31.1)     smoke towards the player, 19 m off
operative  phase 4 act 0  at (-59.1, 31.1)     through it
damage     34 / 34 / 34 from unit -1            inside 14 m and in their sight again: the pistol
kill       attacker -1
```

 The whole checked-in suite was run with these changes; §6.4 has the result.

### 6.3 In the engine (`./scripts/outpost-checks.sh`)

A listen host on `Test.tscn` taking the strategist seat, with `SHOTS` on Xvfb and the Compatibility
renderer: 14 of 14 facts hold — the outpost stands finished, drawn with its dish; it trains the two
units and nothing else; its card offers **Scout Drone (-)** and **Operative (=)**; `-` and `=` queue
them and both come out; the drone climbs to 12.00 m and is still there three seconds later with
nowhere to go, drawn with four rotors; the operative is drawn as a body; a smoke grenade goes off into
one cloud the client draws, which stops a line through it and not one 10 m beside it. The check found
one bug, fixed before it passed: `ExplosionEffects`' table of pools stopped at the wreck, so the cloud
was never drawn (`OutpostTests.EveryExplosionKind_HasAPoolToBeDrawnFrom` now holds the table to the
enum). Its output is `docs/baselines/outpost-checks.txt`, and its screenshots
`docs/baselines/outpost-1-card.png` (the outpost clicked, its card), `outpost-2-trained.png` (the drone
up over it, the operative beside it) and `outpost-3-smoke.png` (the cloud in the foreground).

### 6.4 The checked-in scenario suite

Recorded in the commit that lands this document.

## 7. Not done, and left for a person

- **The computer strategist** neither builds outposts nor trains, patrols or strikes with their units:
  the commander's plan (`StrategistBrain`, `Commander`) knows the three fighting tiers, the builder,
  the supply truck, and the tower's and pillbox's units. A scout drone a scenario gives it hovers where
  it was put and still reveals what it sees; an operative hunts on its own.
- **The map editor** has no outpost prefab: a data map cannot place one finished.
- **Remote clients draw the drone's blast ring at 4 m**, the FPV warhead's, not the charge's 6 m: a hit
  message names a round by its id, and a blast with no round is taken for the FPV drone's. The
  fireball and the damage are right; the listen host draws 6 m.
- **Nobody has played it.** Every number in §2–§4 is a first guess checked against the scenarios
  above, not against people: whether 75 points is cheap for a scout that sees 60 m and can kill, whether
  an operative at 7 m/s with 60 health is a threat or a gift, whether a knife that kills outright is too
  much. The operative's tactics were tuned against players who stand still; against people who move
  and shoot back they have not been watched.
- **Smoke is the operatives'.** The ground force has no smoke grenades, and the strategist's own units
  other than the operative do not throw them.
