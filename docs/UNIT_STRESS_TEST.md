# Unit stress test

Status: **built.** A development scene, `Scenes/Dev/UnitStressTest.tscn`, for finding how many of the
strategist's units can be on the field, fighting, before a frame gets too slow. It is an offline round
on its own small map: the local player is the ground force, with the weapon locker and the vehicle
kiosk, and a panel on `F10` puts strategist units round a barracks — a count of a kind, told what to
do — keeps putting them there, and says what a frame costs while they fight.

Nothing in the game knows it is there. It is not in the main menu or the host panel, it changes no
file under `Scripts/` outside `Scripts/Dev/`, and it drives the round through methods the menu, the
role menu, a barracks and the scenario harness already use (§5).

---

## 1. Running it

| How | |
|---|---|
| The editor | open `Scenes/Dev/UnitStressTest.tscn` and **Run Current Scene** (`F6`) |
| The command line | `godot --path . res://Scenes/Dev/UnitStressTest.tscn` |

The game's own flags still go after a bare `--`: `godot --path . res://Scenes/Dev/UnitStressTest.tscn -- --bots 4`
puts four ground bots beside the player, `-- --bots 0:1` a computer strategist (§2.2).

On the first tick the scene has: started the network offline, as the menu's **Practice offline** does;
told the session its map is this scene; set the round to 240 minutes and 999 tickets; put the local
player on the ground force without the role menu; turned v-sync off. `F1`/`F2`/`F3` still change side.

### 1.1 The scene's settings

On the `UnitStress` node, in the inspector:

| Export | Default | |
|---|---|---|
| `GroundBots` | 0 | ground bots beside the player; `--bots n` wins over it |
| `StrategistBots` | 0 | computer strategists; `--bots n:m` wins over it. One trains its own units and orders the panel's too |
| `RoundMinutes` | 240 | the round's length |
| `GroundForceTickets` | 999 | the ground force's respawns |
| `JoinGroundForce` | on | answers the role menu with the ground force, at every round's start |
| `UncappedFrameRate` | on | v-sync off at the start, so a frame's own cost is what is measured; the panel turns it back on |
| `LogIntervalSeconds` | 5 | seconds between `[stress]` lines in the log while anything is on the field |
| `BarracksPath` | `../Barracks1` | the barracks units are put round |

---

## 2. The scene

A 240 m field with the template's ground and walls (`Scenes/Maps/MapBase.tscn`, docs/MAPS.md §2.2), laid
out north–south so the fight is in front of the spawn:

| | Position (x, z) | |
|---|---|---|
| Ground force spawn line | (0, 70), four markers 4 m apart | facing north (−Z), at the barracks |
| `WeaponLocker1` | (−1.75, 76) | 6 m behind the line, its front to it |
| `VehicleKiosk1` | (1.75, 76) | beside the locker; `Pad4x4` at (12, 72), `PadHelicopter` at (−24, 84), both facing north |
| `Barracks1` | (0, −10), door south at (0, −4) | 80 m from the spawn line |
| Cover | z 20–55 | ten 1.2 m low walls and six 3 m blocks between the barracks and the line, none within 28 m of the barracks |
| `ResourceNode1` | (−45, 15) | for ground bots to go for (§2.2) |
| `StrategistAnchor` | (0, 25) | where the strategist's camera starts, for `F2` |

### 2.1 The barracks has no guns

The template's barracks (`Scenes/Environment/Barracks.tscn`) carries two guns and a roof mortar that reach
107 m: from the spawn line they would be fighting the player as well as the units, and a stress test of
units should measure units. This one is the same 8 × 8 m building and door with no `DefenseMount`
children, so it has no defended ring (docs/MAPS.md §3.2). Units still heal and rearm within 25 m of it,
a barracks' least reach (`Resupply.BarracksMinReachMeters`), which takes in the rings of §3.1.

### 2.2 Bots

None by default: nothing but the panel orders the units, and nothing but the player fights them. Ground
bots (`--bots n`) fight beside the player and go for the node. A computer strategist (`--bots 0:1`) is
the round as it is played — it trains its own units with its points, builds, and orders every unit on
its side into its squads, the panel's included; a unit it has ordered is no longer retargeted by the
panel (§3.1).

---

## 3. The panel

`F10` opens and closes it, and so does `Escape` while it is open (which then does not pause the game).
While it is open the player stands still and the pointer is free, as at the weapon locker
(`InputFocus.Menu`); the round goes on underneath. A line in the top-right corner, always shown, says
`F10 unit stress · 37 units · 58 fps`.

| Control | |
|---|---|
| **Unit** | rifleman, heavy gunner, sniper, operative, technical, tank, supply truck, builder, scout drone, dropship; **Mixed foot** (riflemen, heavy gunners, snipers, operatives, 3:1:1:1); **Mixed army** (the foot and a technical and a tank in every nine) |
| **Count** | 1–64: a wave's size, the count kept alive, a ramp's step |
| **Order** | **Hunt the player** — attack-move on the player, naming them as the target, the point moved after them; **Attack-move on the spawn** — on the spawn line's middle; **Hold round the barracks** — no order: they fight what comes into reach |
| **Tactics** | each unit's micro-autonomy (docs/TACTICS.md §5.9): on, cover and suppression and the rest; off, the order as given |
| **Spawn wave** | puts the count round the barracks now |
| **Clear field** | takes every unit and structure off, as a round's restart does, and stops any mode |
| **Mode**, **Start mode**, **Stop** | §3.2 |
| **Interval** | 5–300 s: between waves, or a ramp's step |
| **Ramp floor** | 10–240 fps: where a ramp stops |
| **V-sync** | on, or off for measuring |

### 3.1 Where units are put

In rings round the barracks' centre: the first 8.5 m out (its building's corner is 5.7 m out), each next
ring one spacing further, spots one spacing apart along each — two of the kind's bodies and a metre,
never under 1.6 m: 1.8 m for riflemen, 3.6 m for tanks — every other ring turned half a spot. A spot is
skipped when a unit already stands within 0.9 spacings of it, or when the navigation mesh's nearest
point is more than 0.75 m from it (it is inside something); otherwise the unit is put on that point.
On open ground 64 riflemen fill the first two rings exactly (29 and 35 spots, out to 10.3 m), and 64
tanks need four (out to 19.3 m).

A hunting unit is given an attack-move on the player's feet with the player named, as a strategist's
right-click on a player gives it; every half second its point is moved after the player once they are
3 m from it. Named, it goes for the player whenever they are in its sensor's reach, and otherwise walks
to the point, fighting what it meets (`UnitManager.AcquireTarget`, `UnitBrain.Destination`). A unit
placed while the player is dead is sent at the spawn line and named once they are back.

Units are not formed into squads: the panel is not a strategist, and a squad's phases are the
commander's (docs/HTN_BOTS.md §4.3). Each unit plans and fights on its own, with its tactics.

### 3.2 The modes

| Mode | |
|---|---|
| **Manual** | the Spawn button and nothing else |
| **A wave every interval** | a wave of the count now and every interval after |
| **Keep the count alive** | every half second, the field topped up to the count of living units |
| **Ramp** | the field topped up to a target that starts at the count and rises by it every interval, measured at each step (§3.3) |

### 3.3 The ramp

Each step lasts the interval. Its first 30 % — at least 2.5 s, at most 5 s — is not measured: the new
units are put down within half a second of the step's start and plan their first paths, and the
engine's numbers of §4 are each the last whole second's, so they need two more seconds to stop showing
it. The rest is: the frames' mean, 95th percentile and worst, the units alive, and the costs of §4. The ramp stops after the first step whose mean frame rate is under the
floor, or after the step at 64; the panel then shows **held N fps up to M units** — the last target
whose step, and every step before it, held the floor — and the log has every step in full:

```
[stress] ramp: +8 every 20 s, floor 45 fps — 41.3 fps at 40 units is under the 45 fps floor
[stress]   target  8 | alive   8.0 |  144.0 fps | frame mean   6.94 ms, p95   7.80, worst  12.10 | unit pass  0.210 ms | worst physics tick   2.40 ms | worst idle frame   6.10 ms | 2160 frames
[stress]   ...
[stress] ramp: held 45 fps up to 32 units
```

(The numbers in that example are illustrative.) A ramp is a measurement of the scene as it is being
played: what the player does — standing still, fighting, looking away — is part of it, and so is what
the units do. For numbers to compare, run it the same way each time: the same kind, order and tactics,
the player standing at the spawn line looking at the barracks.

---

## 4. What is measured

| Number | Where it comes from |
|---|---|
| fps, frame mean, p95, worst | the panel's own window of the last second of rendered frames' lengths (`FrameWindow`): the time between one `_Process` and the next |
| unit pass | `UnitManager.UnitMilliseconds`: the server's pass over every unit — sensing, planning, firing, steering — a tick, averaged over the last 60 ticks |
| worst physics tick | `Performance.TIME_PHYSICS_PROCESS`: the slowest physics step in the last second — the whole simulation tick, the unit pass, every body's move, bots, projectiles and vehicles included. Over 16.7 ms, the 60 Hz simulation fell behind for a tick |
| worst idle frame | `Performance.TIME_PROCESS`: the slowest idle frame in the last second — scripts' `_Process`, the navigation server, and handing the frame to the renderer (`RenderingServer::draw`) |
| draws, objects, primitives | the renderer's counts for the last frame |
| alive, slots | living units, and units on the field alive or dead (§4.2) |

Godot 4.6 sets its two times once a **real** second, each to the slowest step of that second
(`Main::iteration`, `main/main.cpp`), and the unit manager sets the unit pass once every 60 ticks. They
are therefore read as they are — a ramp step keeps the worst it saw and averages the unit pass — and a
ramp's settle is at least 2.5 s so a step does not take in its own placing (§3.3). Under `--fixed-fps`
game time and real time part, and that no longer holds.

The log has a `[stress]` line with all of it every `LogIntervalSeconds` while anything is on the field or
a mode is running, a line for every wave, and the ramp's table when it ends.

### 4.1 Getting numbers that mean something

- **The build.** Run from the editor, the game is the editor's binary and a Debug C# build, both slower
  than an export. The numbers that decide anything come from an exported release build on the machine
  in question, with this scene as its main scene or run with its path.
- **V-sync.** On, a frame waits for the display and a 6 ms frame reads as 16.7 ms; the scene starts with
  it off.
- **`--fixed-fps`.** Every frame is then said to be the same length whatever it took, and the frame rate
  is meaningless; the engine check runs with it (§6) for reproducible game time, and its frame numbers
  are not measurements.
- **Where the time goes.** Frame time falling with the unit pass and the physics tick flat is the
  renderer — the units' bodies, their animation and their shadows; the unit pass rising with the count
  is the simulation. The unit pass is inside the physics tick.

### 4.2 The field's limit

The field holds 64 units (`SimConfig.MaxUnits`), and a dead unit keeps its place for two seconds. A wave
past the limit stops at it and says so on the panel and in the log. The limit is the snapshot's and the
unit pool's; this scene does not change it.

---

## 5. What it touches in the game

No file under `Scripts/` outside `Scripts/Dev/` is changed. The scene's node does what these already do:

| What | Through | As |
|---|---|---|
| the round is offline | `NetworkManager.PlayOffline` | the main menu's **Practice offline** |
| this scene is the session's map | `Session.TrySelectMap` with a `MapInfo` for the scene | the host panel; it is not a catalog entry, so the host panel never lists it and a client is refused it |
| the round's length, tickets and bots | `CombatManager.ApplyHostSettings`, then the copy of the game mode it leaves | the host panel; the copy is this process's, and `Match/default_gamemode.tres` is not written |
| the player on the ground force | `CombatManager.RequestTeam` | the role menu's button |
| a unit put down | `UnitManager.ServerPlaceUnit`, `ServerSetPlacedAutonomy` | the scenario harness (docs/AGENT_API.md §9.1) |
| its order | `Unit.Order` | a barracks giving a fresh unit its rally |
| the field cleared | `UnitManager.ClearUnits` | a round's restart |
| the panel's focus | `InputFocus.BeginMenu`/`EndMenu` | the weapon locker's panel |

One thing it does that no map does: on `_Ready` it builds every CSG shape's geometry at once. A CSG shape
builds it in a deferred call (`CSGShape3D::_make_dirty`), a scene that is the startup scene reaches its
first physics tick before the first deferred call runs, and that tick is where the unit manager bakes
the navigation mesh — so the cover would be missing from the bake (38 vertices instead of 204) and units
would path into it. A map reached through the menu's scene change has a frame between and is not
affected. The engine check holds the bake to this (§6).

| File | |
|---|---|
| `Scenes/Dev/UnitStressTest.tscn` | the scene |
| `Scripts/Dev/UnitStressTest.cs` | the scene's node: the round's set-up, placing, orders, the modes, the log |
| `Scripts/Dev/UnitStressPanel.cs` | the panel |
| `Scripts/Dev/UnitStressRules.cs` | the half without the engine: the rings, the frame window, the ramp |
| `Tests/UnitStressRulesTests.cs` | its tests |
| `tools/EngineChecks/UnitStressChecks.cs`, `scripts/unit-stress-checks.sh` | the engine check |

---

## 6. Checks

**Without Godot:** `dotnet test Tests/Gdpyr.Tests.csproj --filter "FullyQualifiedName~UnitStressRulesTests"`
— the rings' spacing and clearance of the building, that 64 riflemen fit in three rings, the frame
window's mean, percentile, worst and its window being time rather than frames, the costs, and the ramp's
steps, settle, floor, limit and stop.

**In the engine:** `./scripts/unit-stress-checks.sh` (a Godot 4.6 .NET binary on `PATH` or in `$GODOT`)
runs the scene as the startup scene, headless at `--fixed-fps 60`, on a copy of the tree with the check
as an autoload, and holds 39 facts: the round is offline, on this map, with no bots, 999 tickets and 240
minutes, and the player is on the ground force without being asked; the navigation is baked with the
cover carved out of it; one locker; the kiosk's pads pass and it hands out a 4x4 and a helicopter; `F10`
and `Escape` open and close the panel and the focus and pointer follow; a mixed wave of 16 stands in
rings round the barracks, spaced, named on the player with tactics on, and closes 15 m or more on them in
12 s; a wave of 100 stops at 64 and says so; Clear empties the field; Keep alive refills it after four are
killed; a ramp of 16 every 5 s steps 16, 32, 48, 64 and stops at the limit; units sent at the spawn are
sent at its middle. `SHOTS=dir` runs it on a display instead and saves five screenshots.

Run here (4 vCPU Xeon 2.1 GHz, no GPU, the editor binary, a Debug build): both pass, 22 tests and 39
facts, headless and under Xvfb with Mesa's software GL. With the check's `BuildCsgNow` call taken out,
the bake fact fails — the navigation mesh's nearest point to a low wall's middle is the middle itself.
The ramp on the display, mixed foot attacking the spawn with tactics on:

| Units | Unit pass a tick | Worst physics tick | Worst idle frame |
|---|---|---|---|
| 16 | 0.40 ms | 4.1 ms | 144 ms |
| 32 | 0.95 ms | 7.5 ms | 195 ms |
| 48 | 1.40 ms | 5.4 ms | 261 ms |
| 64 | 1.92 ms | 8.7 ms | 242 ms |

Its frame rates are `--fixed-fps`'s and say nothing (§4.1), and the idle frames are Mesa's software
renderer: none of it is a measurement of the game on a player's machine.

What is left for a person: running it on a real machine, from an export, and reading the ramp.
