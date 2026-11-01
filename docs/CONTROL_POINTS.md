# gdpyr — Control points: forward deployment, and the ground force's perks

**Status: built.** A resource node the strategist holds is a door for its barracks (§2), and the
computer strategist uses it (§3). A node can carry a perk for the ground force; the first is the
FPV drone, which kills a tank on a direct hit (§4). `dotnet test`, the game build, the new
scenarios and bot-only rounds before and after are in §5. What a person on the ground force does
against it is **not measured** — see §6.

Companion documents: [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md) §M5 (nodes, capture,
income), [`NETCODE.md`](NETCODE.md) §7 (the message set), [`HTN_BOTS.md`](HTN_BOTS.md) §5.3 (the
commander), [`AGENT_API.md`](AGENT_API.md) §7.2 (button names).

---

## 1. What is asked

The strategist loses every round a person plays on the ground force. Two changes to the control
points (the resource nodes):

1. **Possession enables unit spawn positions, as the barracks is one.** A node the strategist holds
   is somewhere its units can come out.
2. **The ground force holding a node unlocks perks** — different ones — such as an **FPV drone** that
   can blow up the strategist's tanks.

## 2. Forward deployment

| | Rule |
|---|---|
| Where | A barracks whose **rally point is on the pad** of a node the strategist holds puts each finished unit out **at that node** instead of at its door (`ForwardDeploy.Choose`). Of two pads under the rally point, the nearer middle; a tie, the lower index. |
| Denied | A held node with **anybody from the ground force on its pad** deploys nothing — the rule that already stops it paying (`CaptureState.GroundPresent`, set on every occupancy scan). The unit comes out of the door instead. |
| Where on the node | Three rings round the middle, 2.5, 4.3 and 6.1 m out, 1.8 m apart (`ForwardDeploy.Slot`), the first slot on the side facing the rally point. The server takes the first slot that is in the field, not taken this tick, in sight of the node's middle a metre up, on the ground within the pad's height band, on the navigation mesh and clear of the world, units and players — the checks a passenger getting out of a vehicle gets (`UnitManager.TryDeploySlot`). No clear slot: the door. |
| What | Everything a barracks trains **but the dropship**, which takes off from where it is. Towers and pillboxes train at themselves, as before. |
| Who | Any strategist: the rally point is the one the right-click on the ground has always set. A person puts it on a held node; the computer strategist does (§3); an agent's `rally` command does. |
| Shown | The strategist HUD's production line ends `· out at the held node` while it applies. `ServerBarracksState` carries it as one more byte (0 the door, otherwise the node's index plus one). |

Why the rally point rather than a new control: it is already "where my next units go", so putting it
on a node a side holds and having them come out there is the reading a player has before being
told, and it needs no new key, button or message from the client.

## 3. The computer strategist

The HTN commander (`--bot-ai htn`, the default) deploys forward. The legacy strategist does not, and
plays as it did.

- **Home is no longer the rally point.** `Barracks.HomePoint` is the default rally point, ahead of the
  door, wherever the rally point has since been moved; the garrison defends it and the barracks zone
  is centred on it. Before this they were the same point and nothing moved it.
- **Where** (`Commander.ChooseMuster`): none while the garrison is at home and short of its riflemen —
  the next rifleman would join it, and should come out at home. Otherwise a node the side holds,
  uncontested, with no remembered enemy within `ThreatMeters` (60 m), and nearer the ground force's
  spawns than home is; the nearest of those to the spawns. The node chosen last is kept while it
  still qualifies.
- **What it changes**: the barracks' rally point goes on that node (`BotStrategist.Deploy`), and back
  home when there is none. A squad with nothing to do — the one forming from production, a ready one
  with no target — waits **at the node** instead of 10 m in front of home (`CommanderWorld.Muster`),
  which is where the units joining it now come out. A rally point a person sharing the chair moved
  is theirs and is left alone.

## 4. Node perks and the FPV drone

### 4.1 Perks

`ResourceNode.Perk` (`NodePerk`: `None`, `Drone`) is set per node in the map, so different nodes
are worth different things. On the test map the **middle node (0, −35) and the far node (−25, −100)
are drone nodes**; the node nearest the ground force's spawn (−30, −15) has none — a perk is a reason
to push out, not something held from the doorstep. A node with a perk has a post with a ring on it at
the middle of its pad, grey while the ground force does not hold it and lit while it does.

### 4.2 The shelf

The ground force's drones are one shared stock (`DroneStock`), like its ticket pool:

| | |
|---|---|
| Built | One per **120 s of holding a drone node**; both held, one per 60 s. Taking a node gives nothing at once (§5.3 says why). |
| Kept | At most **1** on the shelf. Losing every drone node pauses building where it was, and keeps what is built. |
| Launched | Only **while the ground force holds a drone node**, by a living ground-force player who is not behind a mounted gun or already flying one, 2 s after their last drone ended. |

### 4.3 Flight and warhead

| | |
|---|---|
| Launch | `Q` (`fpv_drone`, input bit 12). It leaves 0.6 m in front of the eye, heading where the player looks. |
| Flying | 24 m/s, always; it turns towards wherever its pilot is looking at up to 3 rad/s (≈170°/s) and never pitches past 83° (`DroneSim.Step`). The mouse that aims a rifle flies it. The pilot sees out of its nose. |
| The pilot's body | Stands where it was, look-only, as a dead player's does, on the server and on its own client — and is the drone's one counter on the field: kill the pilot and the drone falls, doing nothing. |
| It goes off | On the first thing its chord reaches — the world, a player, a unit, a structure (the same `FirstContact` a round uses); on its pilot's **fire** once it has flown 0.5 s; when its **15 s** battery runs out. It is lost without going off if its pilot dies, changes side or the round ends, and if it leaves the field. |
| Warhead | A round in the weapon catalog, id 13 (`Weapons/projectiles/fpv_drone.tres`): **800** to what it struck, and a 4 m blast of 250 falling to nothing at its edge, under the armour rule every explosive is — so a **direct hit kills a 700-point tank**, and a near miss does not. A blast's direct victim takes only the direct damage (an existing rule), which is why the direct figure carries the kill alone. Structures take half. The blast reaches players, the pilot's own side and the pilot as any blast does. The kill is the pilot's, with weapon 13 on the `kill` and `damage` events. |
| Who sees it | Every ground-force client, wherever it is; a strategist where the side's sensors cover it. |

### 4.4 Wire, prediction and the HUD

- **Input**: bit 12 of the existing twelve-byte frame (`InputButtons.Drone`); `drone` among the agent
  API's button names, the playtest harness's and the Python client's.
- **A pilot's frames are sent whole**: the server steers and sets off the drone with their look
  angles and fire button, so the client records and sends them unchanged and simulates its body
  with the look-only half — on the tick, and again in a reconciliation replay. The client learns it
  is flying from the state message, a one-way trip after the server; the body's prediction over
  those ticks is corrected as any misprediction is.
- **`ServerDroneState`**, unreliable at 30 Hz: the shelf (ready, drone nodes held, progress) and
  every drone the recipient may see — pilot, position, heading, seconds of battery (`DroneCodec`,
  4 B + 20 B a drone). A client draws each one carried along its heading for the time since the
  message, at most 150 ms. Not predicted: the drone answers its pilot's mouse a round trip late.
- **HUD**, under the round's line: `[Q] drone`; `drone 40 %` while one is building; `1 drone —
  hold a drone node to launch`; `drone 12 s · fire sets it off` while flying one. The strategist's
  node line ends `· ground force has 1 drone ready` while the ground force could launch one: the
  shelf is in every peer's state message, and a tank's one defence is distance.

### 4.5 Bots

The **HTN ground bots** fly drones (`BotPilot.TrySampleDrone`, `DroneAutopilot`): with one on the
shelf, every half second a bot looks for the nearest strategist tank within 15–140 m with nothing of
the world between its eye and the hull, and nothing of the strategist's on foot within 20 m; it puts
a drone up at it, flies it at the hull, and sets it off if the tank is gone before it arrives. The
**legacy bots do not**: they are what trained policies are measured against, and are unchanged. The
computer strategist does not answer a drone — its units do not shoot at one (§6).

## 5. Verified

### 5.1 Tests and builds

- `dotnet test`: **2,604 passed, 0 failed** (2,548 before; 56 new, counting each theory's cases:
  17 in `ForwardDeployTests`, 39 in `DroneTests`). Covered:
  the deploy rule (held, neutral, ground-held, denied, off every pad, high above one, overlapping
  pads, ties), slots (on the pad, apart, clear of the middle, facing the rally point, wrapping),
  `GroundPresent`; the commander's muster (garrison short, garrison full, contested, not held, enemy
  near, behind home, kept while it qualifies) and idle squads waiting at it while the garrison stays
  home; the drone's launch, airspeed, turn rate, short way round, pitch limit, battery, bounds,
  which endings go off, every launch gate and the relaunch lockout; the shelf (nothing at once, one a
  minute, twice as fast on two, the cap, paused without a node, reset); the state message's round
  trip and four malformed messages; the HUD's lines; the autopilot's gates and its steering passing
  within one tick's travel of the target; bit 12 on the wire and in the agent names; and the files —
  catalog id 13 is the drone, its direct hit is at least a tank's health, a near miss is not, the map's
  middle and far nodes are drone nodes and the near one is not.
- **The tests test something.** One rule broken at a time, each failed at least one of them: a denied
  node deploying; a launch with no drone node held; idle squads waiting in front of home with a
  muster set; a muster chosen while the garrison is short.
- `dotnet build Gdpyr.csproj`: 0 warnings, 0 errors.

### 5.2 Scenarios (Godot 4.6 .NET, headless, through the harness)

- **`drone_kills_tank`** (new): a seat on the middle node with a tank 50 m in front of it presses `drone`
  before the node is taken, about 31 s after and about 123 s after. Seeds 1 and 2: one launch each,
  the third; the drone flies 2.0 s and kills the tank (`unit_lost` tier 2, `kill` weapon 13).
  **Pass.** An earlier cut of the scenario flew into the map's `Range50m` post on x = 0, which is a
  wall to a drone as to a bullet; the line is 4 m to the side. An earlier warhead of 650 left the tank
  at 50: the direct victim is not also in its own blast, so the direct figure is now 800.
- **`htn_strategist_deploys_forward`** (new): the computer strategist, unopposed, its idle opponent
  in the far corner, 200 s. Seeds 1–3: 3 nodes taken; 9 units out of the door — the garrison's four
  riflemen, a tank, a builder and three more riflemen — and then 27 out at a node. **Pass.** Under `--bot-ai legacy` it fails, as an
  `htn_*` claim should: no unit out at a node, and in seed 2 no node taken.
- The existing suite: §5.4.

### 5.3 Bot-only rounds

Five HTN ground bots and an idle seat in the far corner against one HTN computer strategist, a full
20-minute round each, seeds 1–9, through the harness (Godot 4.6 .NET, headless, `--fixed-fps 60` so a
round runs faster than real time), read off each round's event stream — the method of
[`TACTICS.md`](TACTICS.md) T8. The rounds are not reproducible seed for seed, so each column is up
to nine samples, not nine measurements of a seed. The scenario file was a scratch file and is not
committed: the roster above, `hold_still` on the seat at (145, 1, 145), `duration_ticks` 72,300.

| Per round | before | forward deployment only (no drone nodes) | **this branch** | dropped: drone at once on capture, 45 s, shelf 2 | dropped: 60 s, shelf 2 |
|---|---|---|---|---|---|
| Rounds | 9 | 9 | **9** | 4 finished | 9 |
| Strategist wins (ground force out of tickets) | 9 | 9 | **8** | 2 | 3 |
| … at, median (range) [s] | 379 (291–536) | 366 (296–611) | **356 (324–399)** | 355 (326–384) | 340 (306–373) |
| Ground force wins on the clock | 0 | 0 | **1** | 0 | 6 |
| Ground force wins, strategist eliminated | 0 | 0 | **0** | 2 | 0 |
| Ground players killed, mean (range) | 50 (50–50) | 50 (50–50) | **46.8 (21–50)** | 26.5 (1–50) | 19.4 (3–50) |
| Strategist units lost | 23.1 (19–32) | 21.9 (13–37) | **27.2 (21–34)** | 17.5 (9–30) | 12.8 (6–30) |
| Units the barracks trained | 62.6 (43–87) | 57.4 (45–67) | **63.4 (35–80)** | 36.2 (9–68) | 26.4 (9–65) |
| … of them put out at a node | 0 | 35.4 (21–50) | **40.0 (3–49)** | 20.5 (0–41) | 12.6 (0–45) |
| Tanks lost, all rounds · to a drone | 2 · 0 | 1 · 0 | **5 · 5** | 4 · 4 | 7 · 7 |
| Strategist units killed by drones, all rounds | 0 | 0 | **5** | 6 | 8 |
| Node captures, strategist · ground force | 4.2 · 4.1 | 3.7 · 3.7 | **3.7 · 3.8** | 1.8 · 3.2 | 1.8 · 3.6 |

"Put out at a node" counts barracks units whose `unit_built` event is within 12.5 m of a node's
middle; a unit a pillbox or tower trains carries its structure in that field and is not counted.

**What that says.**

- **Forward deployment is used and does what it is for.** Once its garrison is full the computer
  strategist puts about 60 % of what its barracks trains out at a node, and with it alone the
  strategist won all nine rounds as before, a median 13 s sooner, losing about the same. Nine samples
  a side cannot tell 366 s from 379 s apart: what they show is that it does not cost the strategist,
  not that it gains it much against bots it already beats.
- **The drone kills tanks, and how often it can is the whole balance question.** Every tank lost on
  this branch was a drone's (5 in 9 rounds). In bot rounds the strategist's tank is its first big
  purchase — 400 of 1,000 starting points at about 38 s — and the ground bots take all three nodes in
  the first minute. When a drone could be launched within seconds of a capture, or every 30 s with
  two nodes held, that tank died before it did anything in most rounds, the strategist had no
  income and no money to take a node back, and the round ended on the clock or with the strategist
  eliminated: it won **2 of 4** and **3 of 9**. At two minutes a node and one on the shelf it won
  **8 of 9**; the ninth (seed 6) went the same way as the dropped variants', its economy stalled
  after its tank was lost, and the ground force won on the clock with 29 tickets left.
- **What these can and cannot say.** In bot-only rounds the computer strategist already wins every
  round, so they measure whether a change breaks or swings the round, not whether it rescues the
  strategist from a person — the person is what they leave out (§6).

### 5.4 The scenario suite

`./scripts/playtest.sh Tests/Scenarios/*.json` on this branch, headless, `--fixed-fps 60`: **33 of 41
pass**, the two new ones among them. The eight that fail, each also run on the commit this branch
starts from (`3f9d215`) under the same harness and flags:

| Scenario | On `3f9d215` | On this branch |
|---|---|---|
| `locker_launcher_vs_tank`, `locker_resupply`, `locker_resupply_under_fire` | fail, the same claims | fail |
| `htn_strategist_digs_in` | fail, the same claims | fail |
| `tactics_move_to_node` | fail (`squad_drill` phase 5 in 0 of 3 seeds) | fail, the same |
| `technical_wreck_ejects` | fail (seed 2: no wreck, no riders out) | fail, the same |
| `htn_squad_retreats` | pass | failed once (1 of 4 seeds fell back, 2 needed); **passed** when run again |
| `htn_locker_runner_kit` | pass | failed twice on seed 1: see below |

Six fail before this branch as they do on it. The commit the branch starts from added a pillbox
beside the weapon locker and turned the low walls, which is where the four locker and dig-in claims
are made; that is a guess at their cause, not a finding, and they are not this branch's to fix.
`htn_squad_retreats` claims a fall-back in half of four seeds and is as noisy as that sounds.
`htn_locker_runner_kit`: its bot takes the kit at tick 4,900 of 5,160 on the base commit, 260 ticks
from the end; on this branch, seed 1's launcher bot killed its own teammate with a launcher round at
tick 2,301 and the kit was never taken. Nothing this branch changes is in that path — no drone can be
built in the scenario's 86 s, no strategist is in it to deploy anything — and repeated runs of it on
both commits are in §5.5.

### 5.5 Repeats

`htn_locker_runner_kit`, three seeds a run, four more runs on each commit, side by side:

| | runs | failed (`kit.count` 0 on a seed) |
|---|---|---|
| `3f9d215`, with the earlier run | 5 | 3 |
| this branch, with the earlier two | 6 | 4 |

It fails on the commit this branch starts from about as often as on the branch: its bot reaches the
locker near the end of an 86 s window, and whether it gets there in time changes from run to run.

## 6. Not done, and open

- **No person has played it.** The strategist losing to people is the reason for all of this, and
  nothing here measures a person: whether forward deployment gives the strategist enough, and whether
  the drone gives too much back, is a playtest's question. The knobs are `DroneRules` (build time,
  shelf, airspeed, battery) and `fpv_drone.tres` (the warhead).
- **The strategist cannot shoot a drone down.** Units do not target one, and the drone is not in the
  hitbox registry. Its counters are the pilot's body standing still, retaking the drone nodes, the
  two minutes a node takes to build one, and the HUD line that says one is ready. Shooting one down,
  and the computer strategist keeping its tanks back while one is ready, are the obvious next ones.
- **Demos** do not record drones: a replay shows the explosion's hit and the kill, not the flight, and
  the authority's journal holds a pilot's whole frames, so between keyframes a replay walks the
  pilot's body where the round held it still.
- **The agent observation** does not carry drones or the shelf (`gdpyr-agent-obs-4` is unchanged);
  a policy can press `drone` and learns what happened from the events.
- **Only one perk.** The enum, the map's per-node setting and the marker are there for more; the
  request names the drone as an example. The near node has none on purpose (§4.1).
- **The suite's six standing failures** (§5.4) were found here and not fixed: they fail on the
  commit this branch starts from.
- **Not checked with a display**: the pilot's camera, the hidden viewmodel, the marker and the HUD
  line were built headless and compiled, not looked at.
