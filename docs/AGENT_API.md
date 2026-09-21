# gdpyr — Agent API: headless play for external policies

An out-of-band control channel that lets a process which is not a Godot client take a seat in a
round: an RL policy on the ground, an RL policy in the strategist's chair, or a coding agent
running a scripted playtest. The reference point is StarCraft II's `s2client-proto` — a headless
game, a socket, an observation request, an action request, and a step.

Companion documents:
[`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md) §M6–M7 — scope and sequencing ·
[`NETCODE.md`](NETCODE.md) — the tick model, the message set and the fog this API is filtered
through · [`DEPLOYMENT.md`](DEPLOYMENT.md) — the box this must never be exposed on.

---

## 1. Feasibility

**Verdict: feasible, and cheaper here than it would be in most codebases**, because three of the
four hard parts were built for other reasons and already exist. The fourth — the socket, the
serializer and the episode control plane — is the only new construction.

### 1.1 The four seams

| What an agent API needs | Does it exist? | Where |
|---|---|---|
| A place where a player's intent comes from something other than a socket | **Yes** | `PlayerManager.SimulatePlayers` resolves a character's `InputFrame` from one of three sources and one of them is already a brain — `Scripts/Net/PlayerManager.cs:246`, `BotDirector.Sample` at `Scripts/Bots/BotDirector.cs:95` |
| A server-side command entry point that skips the RPC but not the validation | **Yes** | `UnitManager.ServerIssueOrder` / `ServerQueueUnit` (`Scripts/Rts/UnitManager.cs:649`, `:661`) land in the same `ApplyOrder` / `ApplyBuild` a client's RPC does |
| A principled answer to "what is this seat allowed to know" | **Yes** | `VisibilityService` (`Scripts/Match/VisibilityService.cs`) already decides what a strategist's packet may carry, and `BotPilot` already restricts a ground bot to its own eyes (`NETCODE.md` §9) |
| Headless operation with no display and no GPU | **Yes** | `godot --headless -- --server`, shipped since M0 |
| A transport an external process can speak | **No** | ENet, UDP, hand-packed — correct for the game, wrong for Python. §4 |
| Observation serialization and an episode control plane | **No** | §6, §7, §8 |

The first row is the whole reason this is a small feature. The action space of a ground policy is
not a new API surface that has to be kept honest against the game's: it **is** `InputFrame`, the
same twelve bytes a human client sends, consumed by the same movement FSM and the same `WeaponSim`.
`NETCODE.md` §9's claim — "a bot is a player" — is what makes "an external policy is a player"
follow for free. Everything this document adds sits beside `BotPilot`, not beside `PlayerManager`.

### 1.2 The simulation has no RNG of its own

`grep -rn 'GD.Rand\|new Random\|RandomNumberGenerator' Scripts/` returns nothing. Every stochastic
thing in the game — the accuracy cone, a bot's aim error, its strafe, its loiter point — is a hash
of values both ends already compute (`Spread.Seed`, `Scripts/Sim/Spread.cs`), because M2 needed the
shooter's client and the server to derive the same shot. That was a netcode decision, and it hands
this feature a simulation with **no unseeded randomness to chase down**.

What is left is engine nondeterminism, and it is real. §3.

### 1.3 What this is not

- **Not a way to answer "is it fun."** That is playtests with people and the per-round CSV (§M8).
  This API's two jobs are a regression harness that can play the game, and a research affordance.
- **Not a bot-difficulty feature.** `BotTraits.Default` stays the difficulty dial. An attached
  policy replaces a bot's brain; it does not replace the backfill, the roster, or the seat rules.
- **Not a client.** An agent never renders, never predicts, never reconciles. It talks to the
  authority, over loopback, and reads server-authoritative state at whatever fog its seat is behind.

---

## 2. Seats: an agent attaches to a bot

A policy does not connect as a peer and does not get a peer id of its own. It **claims a bot seat**
that `BotDirector` has already created.

```
BotDirector.Sample(peerId, tick)
  ├── seat is attached to an agent  → the frame the agent submitted for this tick   (new)
  └── otherwise                     → BotPilot.Sample(tick)                         (today)
```

One branch, in the one class that already knows bots exist as a category. Everything the seat
already has — a peer id from `BotRoster`'s reserved band, a character, a team, a loadout, a roster
slot, a ticket when it dies, a row in the scoreboard — it keeps. `PlayerManager` is not touched;
`CombatManager` is not touched; no client learns a new message. A round with four humans, two bots
and one attached policy is, on the wire, a round with seven players.

The same shape on the strategist side: an attached strategist seat skips `BotStrategist.ServerTick`
and applies the agent's commands through `ServerIssueOrder` / `ServerQueueUnit` instead, so an
agent's order goes through the identical ownership checks a person's does. Two small additions are
needed for parity, because today's `RequestCancelBuild` and `RequestRally` assume the *local* peer
(`Scripts/Rts/UnitManager.cs:745`, `:758`): `ServerCancelBuild(peerId, index)` and
`ServerSetRally(peerId, index, point)`, each a three-line wrapper over the `Apply*` that already
exists.

### 2.1 Attachment is a lease, not ownership

An agent that stops sending is a body standing in the open, and a round that stalls on a crashed
Python process is worse than no API at all.

- **Real-time mode:** a seat whose agent has not submitted a frame for `AgentGraceTicks` (default
  30 — half a second) falls back to `BotPilot` for that tick and every tick until a frame arrives.
  The seat is not released; the policy can resume mid-round. This is silent by design: a policy
  running slower than 60 Hz is the normal case, not an error.
- **On socket close:** the seat is released outright and reverts to a bot. `BotDirector` then owns
  it again, including giving it up to a person who wants it (`BotFillPolicy`).
- **Stepped mode:** there is no grace. The sim does not advance until every attached stepped seat
  has acted (§5.2), so a hung agent hangs the episode, which is what a training loop wants. A
  configurable `step_timeout_ms` ends the episode with `truncated: true` rather than hanging
  forever.

### 2.2 Seats are requested by role, not by id

`attach` takes a team and a policy kind, and the server picks a free bot seat — spawning one if the
side is under its target and the roster has room. A caller may name a `peer_id` to re-attach to a
seat it held before a reconnect. An agent may not take a seat a person is sitting in; that is the
one refusal `attach` has.

---

## 3. Determinism: what is reproducible and what is not

This is the most important honest caveat in the document, and it decides how the regression harness
in §9 may be written.

**Reproducible:** everything `Scripts/Sim` owns. Movement integration, ballistics, the accuracy
cone, damage, tickets, the economy, build queues, win conditions, every bot decision. These are
pure functions of recorded inputs and seeded hashes, and they are already covered by 34 test files
that run with no engine at all.

**Not guaranteed reproducible:** anything that goes through the engine.

- `MoveAndSlide()` (`Scripts/Fps/fps_controller.cs:343`) runs Godot's kinematic solver against the
  physics server. Contact ordering and solver iteration are not contractually stable across
  versions, and the plan already requires pinning `physics/3d/physics_engine` for exactly this
  reason (§3 of the plan).
- `NavigationAgent3D` pathing: path corridors depend on the baked mesh and on the agent's internal
  state; a unit that repaths one tick later takes a different corner.
- Float determinism across architectures is not claimed by Godot and is not claimed here.

**Consequence.** This is a *stochastic* environment with a *seeded* policy-relevant core, not a
replay-exact one. That is fine for RL — every physics-based RL environment is in this position —
and it is fine for a coding agent's playtest **provided assertions are written against invariants
and distributions, not trajectories**:

| Assert this | Not this |
|---|---|
| "the round ends within 8–20 minutes over 20 seeds" | "the round ends on tick 51,204" |
| "a rifleman at 100 m hits a stationary target ≥ 60% of 50 shots" | "shot 7 hits at position (12.40, 1.75, −8.31)" |
| "the strategist's balance never goes negative" | "the strategist's balance at tick 3600 is 412" |
| "no unit is ever stuck for > 10 s" | "the tank arrives at the node on tick 900" |

`state_hash` (§7) exists to **measure** divergence rather than to promise there is none: the
determinism test in §10 runs the same seeded episode twice in one process and reports the tick at
which the hashes part company. If that tick is consistently late (tens of thousands of ticks), the
engine is more stable than this section assumes and some assertions can tighten. If it is early,
the table above is the contract. Either way the number is measured before anything depends on it.

---

## 4. Transport

A TCP listener inside the server process. Not ENet: the agent channel is reliable, ordered,
request/response, local, and needs to be spoken by Python in twenty lines.

```
gdpyr --headless -- --server 7777 --agent-api 127.0.0.1:7900 [--agent-token <token>]
```

**Framing.** Every frame is length-prefixed, little-endian:

```
u32 length          bytes that follow
u8  kind            1 request · 2 response · 3 observation · 4 event · 5 error
u32 correlation     echoed on the response; 0 for unsolicited frames
... body            UTF-8 JSON, or packed binary for kind 3
```

**Bodies.** The control plane is JSON, always. Observations are JSON or packed `float32`, chosen
per session at the handshake. This is the one design choice that serves both consumers from one
API: `numpy.frombuffer` for a trainer, a readable dict for a coding agent writing a playtest.

**The schema is fetched, not compiled.** `welcome` returns the observation layout — ordered field
names, shapes, dtypes, normalization bounds and a `schema_version` — so the binary path is
self-describing with no protobuf toolchain, no code generation and no version skew that fails
silently. A client that does not recognise `schema_version` refuses to decode rather than
misreading a float.

### 4.1 Security

The agent socket can spawn players, issue orders and reset rounds. On a box with a public Elastic
IP (`DEPLOYMENT.md`) that is remote control of the game server.

- **Default bind is `127.0.0.1`.** A bare `--agent-api 7900` binds loopback.
- **A non-loopback bind without `--agent-token` is a fatal start-up error**, the same way a server
  that cannot bind its UDP port is fatal (`NetworkManager.StartServer`). Not a warning.
- The token is compared in constant time and is the first frame of the session; a connection that
  has not authenticated within 2 s is dropped.
- **`deploy/gdpyr-server.service` never passes `--agent-api`.** Training and playtests run on a
  local box or on a machine whose security group has no inbound rule for the agent port. The one
  inbound rule in `DEPLOYMENT.md` §3 stays the one inbound rule.

---

## 5. Time

### 5.1 Real-time mode (default)

The sim runs at 60 Hz whatever the agents are doing. An action submitted for tick *T* is applied on
the next tick the server simulates; an action that arrives late is applied late; a seat with no
fresh action falls back to its bot (§2.1). This is the mode for **mixed rounds** — a person, five
bots and a policy on the field together — and the mode a coding agent uses when it wants to watch
something happen at the speed it happens.

### 5.2 Stepped mode

The sim advances only when every attached stepped seat has submitted an action for the current
tick. Implemented as a gate in `PlayerManager._PhysicsProcess` (`Scripts/Net/PlayerManager.cs:134`)
before `net.BeginTick()`: if the session is stepped and any seat is pending, the physics frame
returns without advancing the sim tick. The engine frame still happens; the *game* does not.

This is the mode for training and for deterministic-ish regression runs. Note what it does **not**
do: it does not stop a connected human client's clock, which will resync (`SimClock`,
`NETCODE.md` §2) and then be unplayable. Stepped mode therefore **refuses to engage while any
non-agent peer is connected**, and says so in the error rather than quietly producing a bad round.

### 5.3 Action repeat (`step_mul`)

A seat may declare `step_mul: K`. Its action is held for K ticks and it is asked for a new one on
every Kth tick. This is SC2's `step_mul` and it is worth having for the same two reasons: 60
decisions a second is a harder credit-assignment problem than the game needs, and it cuts socket
round-trips by K. Default 4 (15 Hz) for a ground policy; the strategist is naturally slower and
defaults to 30 (2 Hz), which is about what `StrategistTraits.DecisionIntervalTicks` already thinks
a strategist decision is worth.

Held actions are held *exactly* — the same `InputFrame` is re-submitted, so a fire button held for
four ticks produces the same button edges the FSM would see from a human holding it, and not four
separate presses.

### 5.4 Faster than wall clock: a spike, not a promise

**Throughput per instance is 60 ticks of game per second of wall clock, and the first answer to
wanting more is more instances, not a faster one.** A headless gdpyr server has no renderer and is
deliberately capped at 60 FPS so it does not burn EC2 CPU credits (`Scripts/Core/Bootstrap.cs:51`);
how many fit on one box is a measurement the debug HUD's server-frame-time row already supports
(`NETCODE.md` §8), and it should be taken before anyone designs around a number.

Acceleration inside one instance is a **timeboxed spike with a named blocker**, not a planned
feature:

> `MoveAndSlide()` uses the engine's physics delta, not `SimConfig.TickDelta`. Any mechanism that
> makes the engine step faster by changing that delta — raising `physics_ticks_per_second`, or
> `Engine.TimeScale` — silently changes how far a character moves per tick while every other
> integration in the game still uses 1/60. The character would diverge from the ballistics, the
> economy and the tests. So the spike's **first** question is not "does time scale work", it is
> "can movement stop reading the engine's delta" — which means replacing `Move() => MoveAndSlide()`
> with an explicit step at `SimConfig.TickDelta`. That is a netcode change, it affects prediction
> and reconciliation, and it is out of scope until parallel instances are proven insufficient.

---

## 6. Observations

Two observation spaces, one per policy kind. Both are **filtered to what the seat is allowed to
know**, and the filter is the game's own, not a second implementation of it.

### 6.1 Ground-force policy (egocentric)

What a ground bot may see is already settled by `NETCODE.md` §9: its own eyes, at its own sensor
radius, with line of sight. `BotPilot`'s acquisition is that filter, and the observation is built
from the same scan rather than from a fresh look at server state.

| Block | Floats | Contents |
|---|---|---|
| Round | 4 | phase, seconds remaining (normalized), ground tickets / starting, tick parity for `step_mul` alignment |
| Self | 19 | position (3), velocity (3), yaw sin/cos (2), pitch, grounded, movement-state one-hot (6), health fraction, alive, move-speed scale |
| Weapon | 11 | slot one-hot (3), magazine fraction, reserve fraction, reload progress, can-fire, ADS, carrying a gun, mounted on a gun, shots since reload |
| Contacts | 8 × 11 | per contact: is-player, is-unit, hostile, bearing sin/cos, elevation, distance, closing speed, lateral speed, health fraction, ticks since seen |
| Rays | 16 | a horizontal fan ±60° about the look direction, normalized hit distance — the cheap "can I walk that way" signal an FPS policy otherwise has to learn from collisions |
| Objective | 6 | bearing sin/cos (2) and distance to the nearest enemy barracks, nodes held by each side (2), nodes contested |

**144 floats, 576 bytes.** Contacts are the eight nearest the seat has actually acquired, ordered by
distance, zero-padded. A ray fan is 16 physics queries per seat per decision; at `step_mul` 4 and
six seats that is 1,440 queries a second, which is small next to the fog's own ray budget
(`SimConfig.FogLineOfSightCandidates`) but is staggered across seats anyway.

### 6.2 Strategist policy (global, fogged)

Exactly what a human strategist's client is sent, and nothing else. The ground-force contacts come
from `VisibilityService` (`Scripts/Match/VisibilityService.cs:117`), which is the same call that
filters the human's snapshot — so the fog is one implementation, and a change to what a strategist
can see changes what the policy can see on the same commit.

| Block | Floats | Contents |
|---|---|---|
| Round | 8 | phase, seconds remaining, points, income rate, ground tickets, live units, queued units, units lost |
| Own units | 64 × 14 | alive, tier one-hot (3), x, z, health fraction, order one-hot (5) — none/move/attack/patrol/defend — has-target, target distance |
| Barracks | 4 × 7 | x, z, queue depth, head tier, head progress, rally x, rally z |
| Nodes | 8 × 8 | x, z, owner one-hot (3), contested, capture progress, income paid |
| Contacts | 16 × 6 | x, z, visible now, ticks since seen, is-player, team — the ghosts M4 already decays, at the same decay |

**1,092 floats, 4.3 KB** — 8 + 896 + 28 + 64 + 96. The unit block is the whole `SimConfig.MaxUnits`
ceiling, zero-padded, so the tensor shape never changes mid-episode, and the contact block is
`SnapshotCodec.MaxPlayers` (16) for the same reason.

### 6.3 Optional feature planes

Off by default, requested per session: an `N × N × C` grid over the map — own-unit density,
contact density, node ownership, passability — at `N = 32`, `C = 4`. This is SC2's feature-layer
idea and it is what makes a convolutional strategist policy possible at all. It costs a scatter
over live units per observation, which is why it is opt-in rather than always paid for.

### 6.4 The omniscient flag is for debugging and is labelled as cheating

`--agent-omniscient` drops both filters and hands a seat full server state. It exists because "is
the policy losing because it cannot see, or because it is bad" is otherwise unanswerable. Every
observation frame produced under it carries `"omniscient": true`, every episode's trace records it,
and the playtest harness (§9) **fails any scenario that asserts a win under it**, because a result
obtained by cheating is not a result about this game.

---

## 7. Actions and the control plane

### 7.1 Operations

| `op` | Direction | Purpose |
|---|---|---|
| `hello` | → | protocol version, observation format, token |
| `welcome` | ← | tick rate, build id, observation schema, current seed |
| `config` | → | `mode` (realtime/stepped), `step_timeout_ms`, feature planes on/off |
| `list_seats` | → ← | every roster slot: peer id, team, controller (human/bot/agent), alive |
| `attach` | → ← | claim a seat by team + policy kind (+ optional `peer_id`, `step_mul`) |
| `detach` | → | release a seat back to its bot |
| `act` | → | one action for one seat, for one tick |
| `observe` | → ← | the current observation for one seat |
| `step` | → ← | stepped mode: advance N ticks, return each attached seat's observation |
| `reset` | → ← | end the round now and start a fresh one with a given seed and scenario |
| `events` | → ← | every event since a tick (§8) |
| `state_hash` | → ← | a hash of the tick's simulation state, for §3's divergence measurement |
| `quit` | → | close the session; every seat it holds reverts to a bot |

### 7.2 Ground action

The action **is** an `InputFrame`. In binary that is literally the twelve bytes
`Scripts/Sim/InputFrame.cs` defines, prefixed by a seat id; in JSON:

```json
{"op":"act","seat":3,"tick":51204,
 "action":{"move":[0.0,-1.0],"look":{"dyaw":-0.031,"dpitch":0.004},
           "buttons":["sprint","fire"]}}
```

`look` accepts either `{"yaw","pitch"}` absolute — what the wire format carries, and what a replay
needs — or `{"dyaw","dpitch"}` deltas, which is the parameterization a policy should be learning in.
Deltas are **clamped server-side** to the attached seat's turn-rate ceiling.

### 7.3 The fairness ceilings are on by default

An agent that snap-aims across 180° in one tick and fires at the exact instant its crosshair
crosses a head is not playing the game a person plays, and a playtest against it measures nothing.
Every attached seat carries the same ceilings a bot does, from `BotTraits`:

| Ceiling | Default | Source |
|---|---|---|
| Turn rate | 4.5 rad/s | `BotTraits.Default.TurnRateRadians` — about half a mouse |
| Commands per second (strategist) | 8 | new; an APM cap, and a bound on server cost |
| Fire rate, reload, spread | weapon's own | already enforced by `WeaponSim`; no new work |

`--agent-unbounded` lifts the first two, is recorded in the trace exactly as `--agent-omniscient`
is, and is for research runs rather than for playtests. **The ceilings are enforced on the server**,
by clamping the submitted frame, not by trusting the client to have clamped it — the same posture
`NETCODE.md` §1 takes towards every other byte a client chooses.

### 7.4 Strategist action

A list of commands, applied in order, rate-limited by §7.3:

```json
{"op":"act","seat":7,"tick":51204,"action":{"commands":[
  {"cmd":"build","barracks":1,"tier":0},
  {"cmd":"order","kind":"attack","units":[12,13,14],"target":[42.0,-8.5],"target_owner":0},
  {"cmd":"rally","barracks":1,"target":[30.0,-2.0]}
]}}
```

`cmd` is one of `order` (kind: move/attack/patrol/defend/stop), `build`, `cancel`, `rally`, `noop`.
Unit ids that the seat does not own are rejected by `ApplyOrder` exactly as a client's would be,
and the rejection is counted in `UnitManager.RejectedOrders` — so a policy emitting garbage shows
up on the debug HUD rather than silently doing nothing.

---

## 8. Events

The API emits events rather than rewards. **No reward function lives in the game**: what a trainer
values is the trainer's business, and baking one in would be a gameplay claim disguised as
plumbing. What the game owes is the raw record, because otherwise every trainer re-derives kills
from position deltas and gets it slightly wrong.

```json
{"kind":"kill","tick":51204,"attacker":2147483641,"victim":3,"weapon":2,"distance":63.4}
```

| Event | Fields beyond `tick` |
|---|---|
| `round_start` / `round_end` | seed, outcome, duration, the whole `RoundSummary` |
| `kill` / `death` | attacker, victim, weapon, distance |
| `damage` | attacker, victim, amount, remaining |
| `unit_built` / `unit_lost` | unit id, tier, barracks, position |
| `node_captured` / `node_contested` | node, owner, claimant |
| `gun_mounted` / `can_spent` | peer, emplacement |
| `seat_attached` / `seat_released` | seat, peer, reason |

**This event stream is most of M8's per-round CSV.** The columns that milestone names — round
length, ticket curve, strategist income against spending, units built and lost by tier, nodes held
over time, cans spent — are all in the table above, and the counters behind them already exist and
are already labelled for it in the source (`EconomyService.cs:42`, `UnitManager.cs:126`,
`Capture.cs:81`, `BuildQueue.cs:38`, `EmplacementManager.cs:65`). Building this first means M8
becomes a writer over an existing stream rather than a second pass over the whole codebase, which
is the ordering argument for putting the agent API ahead of it.

---

## 9. Playtests for coding agents

The second consumer. A coding agent — Claude Code running in CI or on a branch — should be able to
answer "did my change break the game" without a human, a display, or eight people in a voice call.

### 9.1 Scenario files

A scenario is a JSON file: the seed, the roster, the loadouts, optional scripted spawns, and a list
of assertions. It is checked into the repo beside the tests.

```json
{
  "name": "rifle lethality at 100 m",
  "seed": 7,
  "duration_ticks": 1200,
  "roster": {"ground": 1, "strategists": 0},
  "seats": [{"team":"ground","policy":"ground","script":"scripts/hold_and_fire.json"}],
  "spawns": [{"peer":"agent0","at":[0,1,0],"look":[0,0]},
             {"unit":"infantry","team":"strategist","at":[0,1,-100],"order":"none"}],
  "assert": [
    {"metric":"events.kill.count","op":">=","value":1},
    {"metric":"observation.self.health","op":"==","value":100},
    {"metric":"events.damage.sum","op":"between","value":[100,400]}
  ]
}
```

Run it:

```bash
./scripts/playtest.sh Tests/Scenarios/rifle_lethality.json
# rifle lethality at 100 m .......... PASS  (1,200 ticks, 20.0 s sim, 3.1 s wall)
#   events.kill.count            1 >= 1          ok
#   observation.self.health      100 == 100      ok
#   events.damage.sum            212 in [100,400] ok
```

Exit code 0 on pass, 1 on any failed assertion, 2 on a harness error. That is the whole CI contract,
and it is the thing that makes this usable from an agent loop: a deterministic exit code, a
machine-readable summary on `--json`, and a trace file to re-run.

### 9.2 What makes it usable rather than merely possible

- **It is synchronous.** `reset → step(n) → observe → assert` with no sleeps and no polling. Stepped
  mode (§5.2) is what buys this; wall-clock waits in a test are how a suite becomes flaky.
- **It reads back.** `observe` in JSON mode returns named fields, so an assertion is
  `observation.self.health`, not offset 37 of a float array.
- **It fails with a trace.** Every run writes `trace-<seed>.jsonl`: every action, every event, the
  `state_hash` per second. A failed assertion prints the tick it failed on and the path to the
  trace, and the trace replays.
- **It is honest about §3.** The assertion vocabulary has `between`, `percentile` and `over_seeds`
  precisely so that the natural thing to write is a distributional claim. There is deliberately no
  `assert_position_equals`.
- **It needs no Godot install to author.** Scenario files are data; the schema is published at
  `welcome`. Only running one needs the exported headless server, which `scripts/export-server.sh`
  already builds.

### 9.3 The Python client

`tools/gdpyr_env/` — a single module, no dependencies beyond `numpy`:

```python
from gdpyr_env import GdpyrEnv

env = GdpyrEnv(port=7900, seat="ground", step_mul=4)
obs, info = env.reset(seed=7)
for _ in range(900):
    obs, events, terminated, truncated, info = env.step(env.action_space.sample())
    if terminated or truncated:
        obs, info = env.reset()
```

Gymnasium-shaped because that is what every trainer already accepts, but the shape is a thin wrapper
over §7's operations and the protocol is usable without it.

---

## 10. Tests

The same rule the rest of the codebase follows: the interesting part is engine-free and answered by
`dotnet test`, with no Godot install.

| Test | Lives in |
|---|---|
| Frame codec round-trips, including every button bit and the sbyte axis quantization | `Tests/AgentCodecTests.cs` |
| Observation encoder: known state in, known float vector out; padding, ordering by distance, zero-fill | `Tests/AgentObservationTests.cs` |
| The schema `welcome` publishes matches what the encoder writes — field count, offsets, bounds | `Tests/AgentObservationTests.cs` |
| Turn-rate and APM clamps: a 180° snap arrives as 4.5 rad/s, nine commands become eight | `Tests/AgentLimitsTests.cs` |
| Seat lease: grace expiry falls back to the pilot, socket close releases, a person's seat is refused | `Tests/AgentSeatTests.cs` |
| Fog parity: the strategist observation contains exactly the peers `VisibilityService.IsVisibleTo` admits | `Tests/AgentObservationTests.cs` |
| Scenario parser and the assertion vocabulary, including `between` and `over_seeds` | `Tests/ScenarioTests.cs` |

One test is not engine-free and is worth the exception: the **divergence probe** of §3 — two seeded
600-tick episodes in one headless process, comparing `state_hash` per tick and reporting the first
disagreement. It runs under `scripts/playtest.sh`, not `dotnet test`, and its output is a number to
be believed rather than a pass/fail.

---

## 11. Message set

Mirroring `NETCODE.md` §7, so the two can be read together. All of it is loopback TCP and none of it
touches the UDP budget in that section.

| Frame | Direction | Rate | ~Size |
|---|---|---|---|
| `act` (ground, binary) | A→S | 15 Hz per seat at `step_mul` 4 | 16 B |
| `act` (strategist, JSON) | A→S | 2 Hz per seat | ~200 B |
| Observation (ground, binary) | S→A | 15 Hz per seat | 576 B |
| Observation (strategist, binary) | S→A | 2 Hz per seat | 4.3 KB |
| Feature planes (optional) | S→A | with the observation | 16 KB |
| Event | S→A | per event | ~120 B |
| Control (`attach`, `reset`, `step`, …) | both | per call | < 1 KB |

Six ground seats and one strategist, binary, no feature planes:
6 × 576 B × 15 Hz + 4,368 B × 2 Hz ≈ **61 KB/s over loopback.** The bound on throughput is the
60 Hz simulation, never this socket.

---

## 12. Open questions

1. **How far apart do two seeded episodes actually drift?** §3 and §10 measure it; nothing should be
   designed around an answer until it is measured.
2. **How many headless instances fit on one box?** The debug HUD reports server frame time
   (`NETCODE.md` §8); take the number before planning a training run.
3. **Is the ray fan the right local observation, or should it be a small occupancy patch?** 16 rays
   is the cheap answer and is what M6 ships; a policy that keeps walking into walls is the signal to
   revisit.
4. **Should an attached policy be visible to clients?** Today it is indistinguishable from a bot,
   which is correct for playtests and possibly wrong for a human who wants to know what they are
   fighting. A flag in the scoreboard row is one byte if it is ever wanted.
5. **Self-play against the human seat.** Nothing prevents two attached policies on opposite sides;
   whether the round is worth watching is a question for after M6.
