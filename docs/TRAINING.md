# gdpyr — Training agents

How to attach a reinforcement-learning policy to a gdpyr seat and train it, using
[RLMatrix](https://github.com/asieradzk/RL_Matrix) — deep RL in C#, on TorchSharp,
battle-tested in Godot. No Python anywhere.

Companion documents:
[`AGENT_API.md`](AGENT_API.md) — the protocol this is built on ·
[`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md) §M6 — scope and sequencing ·
[`NETCODE.md`](NETCODE.md) — the tick model and the fog a seat sits behind.

---

## 1. The shape of it

A gdpyr server does not train anything and knows nothing about learning. It opens
a socket, hands out seats, emits observations and events, and applies whatever
actions arrive — and every one of those is the same thing a bot or a person
already does. The learner is a **separate process** that speaks that socket.

```
┌──────────────────────────────┐        ┌─────────────────────────────────┐
│ godot --headless -- --server │  TCP   │ gdpyr-train                     │
│   --agent-api 7900           │◄──────►│   Gdpyr.AgentClient (socket)    │
│                              │  loop  │   GdpyrGroundEnv : IEnvironment │
│   6 bots + 1 strategist bot  │  back  │   RLMatrix  PPO / DQN           │
│   + your policy's seat       │        │   TorchSharp                    │
└──────────────────────────────┘        └─────────────────────────────────┘
```

Three projects under `tools/`, and the split is deliberate:

| Project | Depends on | What it is |
|---|---|---|
| `Gdpyr.AgentClient` | `RLMatrix.Common` only | The protocol client, the observation decoder, and `GdpyrGroundEnv : IEnvironmentAsync<float[]>`. A few hundred kilobytes; no TorchSharp. |
| `Gdpyr.Trainer` | the client + `RLMatrix` | The console app that builds a PPO or DQN agent and runs the rollout loop. This is where libtorch is paid for. |
| `Gdpyr.Probe` | the client only | The divergence probe (§7). Needs no libtorch, because the number it measures decides how every later assertion may be written. |

The game assembly never references RLMatrix or TorchSharp, and `Gdpyr.csproj`
excludes `tools/**` from its compile glob so it cannot start.

## 2. Setting up

You need the .NET 8 SDK and a Godot 4.6 .NET binary. Then:

```bash
dotnet build tools/Gdpyr.AgentClient     # light; no libtorch
dotnet build tools/Gdpyr.Probe           # light
dotnet build tools/Gdpyr.Trainer         # pulls libtorch: see the warning below
```

> **The first `Gdpyr.Trainer` restore downloads several gigabytes.** RLMatrix's
> package depends on `TorchSharp-cuda-windows`, which pulls a Windows CUDA
> redistributable split across a dozen NuGet packages. On Windows with an NVIDIA
> card that is exactly what you want. Everywhere else it is both unusable *and* a
> hard build error, because libtorch refuses to have two runtime packages
> referenced at once.
>
> `Gdpyr.Trainer.csproj` handles this with a `TorchBackend` property that
> defaults to `cuda-windows` on Windows and `cpu` elsewhere. The `cpu` path
> excludes the CUDA package's build assets and references `TorchSharp-cpu`
> instead. NuGet still *downloads* the CUDA package — that is RLMatrix's
> packaging and not something this repository can undo. Override it explicitly
> with:
>
> ```bash
> dotnet build tools/Gdpyr.Trainer -p:TorchBackend=cuda-windows
> dotnet build tools/Gdpyr.Trainer -p:TorchBackend=cpu
> ```

You will also see `warning CS9057` about an analyzer built against a newer
Roslyn. It comes from one of RLMatrix's transitive dependencies, is harmless, and
goes away on a newer SDK.

## 3. Training

The short version:

```bash
GODOT=/path/to/Godot_mono ./scripts/train.sh
```

That starts a headless server with the agent channel on loopback, fills the other
side with bots, attaches one ground seat, and trains PPO against it. Everything
after the recognised flags is passed through to the trainer:

```bash
./scripts/train.sh --algo dqn --steps 200000 --save runs/dqn
./scripts/train.sh --ports 7900,7901,7902,7903          # four servers, one learner
./scripts/train.sh --lr 3e-4 --width 1024 --step-mul 4
```

Or drive it by hand, which is what the script does:

```bash
godot --path . --headless -- --server 7777 --agent-api 7900 --bots 6:1 &
dotnet run --project tools/Gdpyr.Trainer -c Release -- --port 7900 --algo ppo
```

| Flag | Default | What it does |
|---|---|---|
| `--port 7900[,7901,…]` | `7900` | One agent-api port per server. Every environment feeds one learner. |
| `--algo ppo\|dqn` | `ppo` | PPO first: the reward is dense enough to have a gradient. |
| `--steps` | `100000` | Rollout steps, not ticks and not episodes. |
| `--step-mul` | `4` | Ticks one decision is held for. 4 is 15 Hz (`AGENT_API.md` §5.3). |
| `--episode-steps` | `1800` | Decisions before the episode is truncated and the round restarted. |
| `--save <dir>` / `--load <dir>` | – | A **directory**: RLMatrix writes one file per network and numbers them. |
| `--play` | off | Run a loaded policy without learning from it. |
| `--realtime` | off | Do not engage stepped mode. For watching, not for training — see §5. |
| `--lr`, `--width`, `--seed`, `--report-every`, `--save-every` | | The usual. |

RLMatrix also tries to reach its dashboard on `localhost:7126` and prints
`Error starting dashboard connection` when nothing is there. Harmless; run the
dashboard if you want the curves.

## 4. Scaling out

**Throughput per instance is 60 ticks of game per second of wall clock, and the
first answer to wanting more is more instances, not a faster one**
(`AGENT_API.md` §5.4). A headless gdpyr has no renderer and is capped at 60 FPS
so it does not burn EC2 CPU credits; making the engine step faster would change
how far a character moves per tick while every other integration still uses 1/60,
which is a netcode change and out of scope.

So: run four servers and point one learner at all four.

```bash
./scripts/train.sh --ports 7900,7901,7902,7903 --steps 500000 --save runs/ppo
```

`train.sh` gives each server its own game port (`--game-port`, default 7777,
incremented per instance) so the UDP listeners do not collide. How many fit on
one box is a measurement, not a guess: the server frame time row on the debug HUD
(`NETCODE.md` §8) is what to take it with.

RLMatrix also has a networked trainer (`RLMatrix.Remote` +
`RemoteDiscreteRolloutAgent`, over SignalR) for spreading environments across
machines. `GdpyrGroundEnv` is an ordinary `IEnvironmentAsync<float[]>`, so it
drops into that constructor unchanged; this repository does not ship a
configuration for it.

## 5. Stepped versus real time, and why it matters

**Train in stepped mode.** It is the default.

In real time the simulation runs at 60 Hz whatever the learner is doing. If an
optimizer step takes longer than the seat's grace window — half a second — the
seat falls back to its bot for the intervening ticks (`AGENT_API.md` §2.1). The
round is fine; the *transition the trainer recorded* is not the transition the
game played, because a bot chose some of it. That is silent, it is the normal
case for anything slower than 60 Hz, and it quietly corrupts a replay buffer.

In stepped mode the simulation does not advance until the trainer asks it to, so
every transition is exactly the one the policy caused. The cost is that a hung
trainer hangs the round, which is why `step_timeout_ms` exists: past it the
session is told and dropped back to real time rather than the server standing
still for ever.

Stepped mode **refuses to engage while a human peer is connected**, and drops an
already-stepped session back to real time if one arrives. A person's clock would
resync and the round would be unplayable.

Use `--realtime` when you want to watch a trained policy play alongside people.

## 6. What the policy sees, does and wants

Three files, and none of them is the game's opinion.

### Observation — 145 floats

Built by the server, decoded by the client against the schema `welcome`
publishes. The layout is in `AGENT_API.md` §6.1; the short version is: round
state, self, weapon, the eight nearest contacts **its own eyes have acquired**, a
16-ray forward fan, and the objective.

Two things worth knowing:

- The fog is the game's own. Contacts come from `GroundSensor`, which is the same
  scan a bot acquires through — so a policy and a bot are looking at one world
  through one pair of eyes. `--agent-omniscient` lifts it, stamps `omniscient` on
  every observation frame, and makes any result obtained under it a result about
  a different game.
- **The design says 144 floats; the shipped vector is 145.** The movement-state
  one-hot is seven wide, not six, because the FSM has seven states. Decode by the
  schema, not by the document — that is what the schema is for, and the client
  refuses to decode a `schema_version` it does not recognise.

### Action — `tools/Gdpyr.AgentClient/GroundActionSpace.cs`

The game's action space *is* `InputFrame`: continuous look deltas and a button
field. RLMatrix's discrete agents want a vector of small integers and **require
every head to be the same width**. So six heads of five:

| Head | 0 | 1 | 2 | 3 | 4 |
|---|---|---|---|---|---|
| forward | full back | half back | still | half forward | full forward |
| strafe | full left | half left | still | half right | full right |
| turn | −1 | −0.35 | 0 | +0.35 | +1 × one decision's turn ceiling |
| pitch | −0.5 | −0.2 | 0 | +0.2 | +0.5 × the same |
| trigger | none | fire | ads | ads+fire | reload |
| stance | none | sprint | crouch | jump | use |

Turn steps are fractions of the **ceiling**, not of a circle: an attached seat is
clamped server-side to `BotTraits.Default.TurnRateRadians` — 4.5 rad/s, about
half a mouse — and a policy that learned to snap would not transfer to a round
with the ceilings on. `--agent-unbounded` lifts it for research runs, is stamped
into the handshake and into every trace, and the action space still assumes 4.5
so the two stay comparable.

### Reward — `tools/Gdpyr.AgentClient/GroundReward.cs`

**The game emits events and no rewards, on purpose.** What a trainer values is
the trainer's business, and baking a reward function into the server would be a
gameplay claim disguised as plumbing (`AGENT_API.md` §8). Every number in that
file is a hypothesis, and it is one file so you can disagree with it in one
place:

| Term | Default | |
|---|---|---|
| kill | `+2` | a unit or a person, attributed to this seat |
| damage dealt | `+0.01` per point | to a unit; every unit belongs to the strategist |
| friendly damage | `−0.03` per point | to a player; every body on the field is ground force |
| damage taken | `−0.01` per point | |
| death | `−1` | |
| objective progress | `+0.5` per normalized unit closed | the one dense term, and the only shaping |
| step cost | `−0.001` | so dawdling is not free |
| round won / lost | `+5` / `−5` | at `round_end`, by outcome |

The dense objective term is a bet that walking towards the enemy barracks is
roughly right. It is what stops a fresh policy standing still for ever; it is also
the first thing to delete when you want to find out what the policy has actually
learned.

## 7. Determinism: measure it before you rely on it

`Scripts/Sim` is reproducible — movement integration, ballistics, the accuracy
cone, damage, tickets, the economy, build queues, win conditions, every bot
decision. `MoveAndSlide()` and `NavigationAgent3D` are not. So the design
(`AGENT_API.md` §3) refuses to promise reproducibility and ships a probe to
measure it instead:

```bash
GODOT=/path/to/Godot_mono ./scripts/divergence.sh --ticks 600
```

It attaches a seat, holds it on a fixed script, runs the same seeded episode
twice and reports the first tick at which the two state hashes disagree.

**The measured answer today is: they disagree from the first tick.** Not because
the engine drifts that fast, but because there is nothing to drift *from* —
`reset` starts a *fresh* round, not a repeatable one. Every stochastic decision in
the game is a hash of the **absolute server tick** (`Spread.Seed` — the accuracy
cone, a bot's aim error, its strafe, its loiter point), and the server tick never
rewinds. Two episodes therefore never share a starting state, whatever seed you
pass.

That is a real finding and it has two consequences:

1. **Write distributional assertions.** "the round ends within 8–20 minutes over
   20 seeds", not "the round ends on tick 51,204". §3 of the design has the full
   table. The `seed` on `reset` labels an episode and is echoed on `round_start`;
   it does not determine one.
2. **M7 considered keying those hashes on ticks since the round started, and
   decided against it** (`AGENT_API.md` §12.1). It would not make an episode
   reproducible — `MoveAndSlide()` and `NavigationAgent3D` are the other half of
   the problem — and it would hand every episode the same bot aim error and the
   same strafe at the same moment, so a policy would see one fixed noise sequence
   instead of a distribution to generalize over. If you want repeatability badly
   enough to pay for it anyway, the change is in `BotBrain`, `BotPilot` and the
   tests around both, and you should expect your seed sweep to stop meaning what
   it meant.

## 8. Security

The agent socket can spawn players, issue orders and reset rounds. On a box with
a public Elastic IP that is remote control of the game server.

- **The default bind is loopback.** `--agent-api 7900` binds `127.0.0.1`.
- **A non-loopback bind without `--agent-token` is a fatal start-up error**, the
  same way a server that cannot bind its UDP port is fatal. Not a warning.
- The token is compared in constant time, must arrive in the first frame, and a
  connection that has not authenticated within two seconds is dropped.
- **`deploy/gdpyr-server.service` never passes `--agent-api`.** Train on a local
  box, or on one whose security group has no inbound rule for the agent port. The
  one inbound rule in [`DEPLOYMENT.md`](DEPLOYMENT.md) §3 stays the one inbound
  rule.

## 9. Writing your own client

You do not need this library, and for a scripted playtest you may not want it.
The protocol is length-prefixed frames with a JSON control plane
(`AGENT_API.md` §4, §7) and it is deliberately small enough to speak from
anything with a TCP socket:

```
u32 length          bytes that follow
u8  kind            1 request · 2 response · 3 observation/packed action · 4 event · 5 error
u32 correlation     echoed on the response; 0 for unsolicited frames
... body            UTF-8 JSON, or packed float32 / packed action for kind 3
```

```
→ {"op":"hello","protocol":1,"observations":"json"}
← {"op":"welcome","tick_rate":60,"schema":{…},"turn_rate_radians":4.5,…}
→ {"op":"config","mode":"stepped"}
→ {"op":"attach","team":"ground","policy":"ground","step_mul":4}
← {"op":"attached","seat":1073741824,"step_mul":4}
→ {"op":"act","seat":1073741824,"action":{"move":[0,-1],"look":{"dyaw":-0.031},"buttons":["fire"]}}
→ {"op":"step","n":4}
← {"op":"step","tick":51208,"truncated":false,"seats":[…]}
→ {"op":"observe","seat":1073741824}
← {"op":"observation","seat":…,"tick":…,"observation":{"self.health":1.0,…}}
```

Ask for `"observations":"json"` and an observation comes back as **named fields**
— `observation.self.health`, not offset 37 of a float array — which is what makes
a scripted playtest readable. Ask for `"binary"` and it is packed float32 behind
a twelve-byte header, which is what a trainer wants.

`tools/Gdpyr.AgentClient/AgentProtocol.cs` implements the framing and the packed
action a second time, deliberately: it is what an outside process links, and a
client compiled against the game's own source would hide exactly the version skew
the published schema exists to catch. `Tests/AgentInteropTests.cs` checks the two
implementations agree, byte for byte.

## 10. Strategist policies

Shipped in M7. `attach` with `policy: "strategist"` claims a strategist's chair,
the observation is 1,092 floats behind the same `VisibilityService` a human
strategist is behind, and an `act` carries a list of commands that run through
the same `ServerIssueOrder` / `ServerQueueUnit` a person's RPC lands in
([`AGENT_API.md`](AGENT_API.md) §6.2, §7.4).

Three differences from a ground seat are worth knowing before you point a learner
at one:

- **The action is a command list, and it is consumed rather than held.** A ground
  action repeats for the seat's `step_mul`, because that is what a held fire
  button is; a command list run thirty times would queue thirty riflemen. A
  strategist seat that submits nothing on a decision is a strategist that decided
  to do nothing, and only falls back to `BotStrategist` once it goes quiet for
  its grace window (§2.1).
- **The APM cap is the ceiling that bites**, not the socket: eight commands a
  second, spent when the tick runs the list. A policy emitting more is not
  disconnected — the surplus is dropped and counted.
- **The feature planes are the strategist's**, off by default: `config`
  `{"feature_planes": true}` attaches a 32×32×4 grid to the observation frame
  (§6.3). A ground policy asking for them does not get them; its spatial signal
  is the ray fan in its own vector.

`GdpyrGroundEnv` has no strategist twin in `tools/Gdpyr.AgentClient` yet: the
discretization of a command list is a research decision rather than a plumbing
one — how many barracks, how many unit groups, how coarse a target grid — and
baking one in would be the same mistake as baking in a reward. What is there is
`GdpyrConnection.AttachStrategistAsync` and `ActCommands`, which is the protocol
without the opinion. `tools/gdpyr_env/` speaks both seats from Python.

## 11. Playtests, which are not training

A scenario file plays a scripted round against a headless server and asserts
something about the result, with no display and nobody watching:
`./scripts/playtest.sh Tests/Scenarios/rifle_lethality.json`. It shares this
socket and nothing else — no libtorch, no learner, and a deterministic exit code
([`AGENT_API.md`](AGENT_API.md) §9). If a training run stops learning, that is
the first place to check whether the *game* changed under it.
