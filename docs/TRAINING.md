# gdpyr — Training agents

How to attach a reinforcement-learning policy to a gdpyr seat — **either seat** —
and train it, using [RLMatrix](https://github.com/asieradzk/RL_Matrix): deep RL in
C#, on TorchSharp, battle-tested in Godot. No Python anywhere.

Companion documents:
[`RL_ARCHITECTURE.md`](RL_ARCHITECTURE.md) — which algorithm to run, and why PPO
is still the default ·
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
┌──────────────────────────────┐        ┌──────────────────────────────────────┐
│ godot --headless -- --server │  TCP   │ gdpyr-train                          │
│   --agent-api 7900           │◄──────►│   Gdpyr.AgentClient (socket)         │
│                              │  loop  │   GdpyrGroundEnv      : IEnvironment │
│   6 bots + 1 strategist bot  │  back  │   GdpyrStrategistEnv  : IEnvironment │
│   + your policy's seat(s)    │        │   RLMatrix  PPO / DQN · TorchSharp   │
└──────────────────────────────┘        └──────────────────────────────────────┘
```

Three projects under `tools/`, and the split is deliberate:

| Project | Depends on | What it is |
|---|---|---|
| `Gdpyr.AgentClient` | `RLMatrix.Common` only | The protocol client, the observation decoder, and the two environments — `GdpyrGroundEnv` and `GdpyrStrategistEnv`, both `IEnvironmentAsync<float[]>`. A few hundred kilobytes; no TorchSharp. |
| `Gdpyr.Trainer` | the client + `RLMatrix` | The console app that builds a PPO or DQN agent and runs the rollout loop. This is where libtorch is paid for. |
| `Gdpyr.Probe` | the client only | The divergence probe (§10). Needs no libtorch, because the number it measures decides how every later assertion may be written. |

The game assembly never references RLMatrix or TorchSharp, and `Gdpyr.csproj`
excludes `tools/**` from its compile glob so it cannot start.

## 2. Setting up

You need the .NET 8 SDK and a Godot 4.6 .NET binary. Then:

```bash
dotnet build tools/Gdpyr.AgentClient     # light; no libtorch
dotnet build tools/Gdpyr.Probe           # light
dotnet build tools/Gdpyr.Trainer         # pulls libtorch: see the warning below
dotnet test                              # the action spaces and rewards, with no server
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

The short version, for each seat:

```bash
GODOT=/path/to/Godot_mono ./scripts/train.sh                        # a ground seat
GODOT=/path/to/Godot_mono ./scripts/train.sh --policy strategist    # the other chair
```

That starts a headless server with the agent channel on loopback, fills the rest
of the round with bots, attaches one seat, and trains PPO against it. Everything
after the recognised flags is passed through to the trainer:

```bash
./scripts/train.sh --algo dqn --steps 200000 --save runs/dqn
./scripts/train.sh --ports 7900,7901,7902,7903          # four servers, one learner
./scripts/train.sh --history 4 --lr 3e-4 --width 1024
./scripts/train.sh --policy strategist --metrics runs/strategist.csv
```

Or drive it by hand, which is what the script does:

```bash
godot --path . --headless -- --server 7777 --agent-api 7900 --bots 6:1 &
dotnet run --project tools/Gdpyr.Trainer -c Release -- --port 7900 --policy ground
```

| Flag | Default | What it does |
|---|---|---|
| `--port 7900[,7901,…]` | `7900` | One agent-api port per server. Every environment feeds one learner. |
| `--policy ground\|strategist` | `ground` | Which seat to sit in. Decides the observation, the action space and the reward. |
| `--algo ppo\|dqn` | `ppo` | PPO first; the argument is [`RL_ARCHITECTURE.md`](RL_ARCHITECTURE.md) §1. |
| `--steps` | `100000` | Rollout steps, not ticks and not episodes. |
| `--step-mul` | `4` / `30` | Ticks one decision is held for. 4 is 15 Hz on the ground; 30 is 2 Hz in the chair. |
| `--episode-steps` | `1800` / `2400` | Decisions before the episode is truncated. The strategist's default is a whole round. |
| `--history` | `1` | Observations stacked into one state (§6, [`RL_ARCHITECTURE.md`](RL_ARCHITECTURE.md) §4). |
| `--save <dir>` / `--load <dir>` | – | A **directory**: RLMatrix writes one file per network and numbers them. |
| `--play` | off | Run a loaded policy without learning from it. |
| `--episodes N` | – | Stop after N finished episodes, whatever `--steps` says. For evaluation (§9). |
| `--metrics <file>` | – | Append a CSV row per report window: step, episodes, reward, wins, losses, fallbacks. |
| `--realtime` | off | Do not engage stepped mode. For watching, not for training — see §5. |
| `--step-timeout <ms>` | `10000` | How long the server holds the sim for this learner. Raise it when two share one (§8). |
| `--no-reset` | off | Never restart the round; follow the learner that does (§8). |
| `--lr`, `--width`, `--seed`, `--report-every`, `--save-every` | | The usual. |

A run prints a line every `--report-every` steps and a summary at the end:

```
step 5000/200000 | episodes 12 | last episode 41.30 | reward in flight 3.90 | wins 5/9
ground: 12 episodes, 9 decided, 5 won, 4 lost, win rate 55.6%
```

**Reward is the number this repository made up; the win rate is the game's.** §9
is about telling them apart. RLMatrix also tries to reach its dashboard on
`localhost:7126` and prints `Error starting dashboard connection` when nothing is
there — harmless, and `--metrics` is the offline alternative.

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

What that costs in wall clock, per seat, is the table in
[`RL_ARCHITECTURE.md`](RL_ARCHITECTURE.md) §5 — and for a strategist, whose
episode is a whole twenty-minute round, the other lever is `RoundDurationMinutes`
in `Match/default_gamemode.tres`. Shortening it for training is a curriculum
choice; leaving it shortened for the final evaluation is a lie.

RLMatrix also has a networked trainer (`RLMatrix.Remote` +
`RemoteDiscreteRolloutAgent`, over SignalR) for spreading environments across
machines. Both environments are ordinary `IEnvironmentAsync<float[]>`, so they
drop into that constructor unchanged; this repository does not ship a
configuration for it.

## 5. Stepped versus real time, and why it matters

**Train in stepped mode.** It is the default.

In real time the simulation runs at 60 Hz whatever the learner is doing. If an
optimizer step takes longer than the seat's grace window — half a second — the
seat falls back to its bot for the intervening ticks (`AGENT_API.md` §2.1). The
round is fine; the *transition the trainer recorded* is not the transition the
game played, because a bot chose some of it. That is silent, it is the normal
case for anything slower than 60 Hz, and it quietly corrupts a replay buffer.
`gdpyr-train` prints `bot fallbacks` when the server reports any, which is the
cheapest way to catch it.

In stepped mode the simulation does not advance until the trainer asks it to, so
every transition is exactly the one the policy caused. The cost is that a hung
trainer hangs the round, which is why `step_timeout_ms` exists: past it the
session is told and dropped back to real time rather than the server standing
still for ever.

Stepped mode **refuses to engage while a human peer is connected**, and drops an
already-stepped session back to real time if one arrives. A person's clock would
resync and the round would be unplayable.

Use `--realtime` when you want to watch a trained policy play alongside people.

## 6. The ground seat: what the policy sees, does and wants

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

`--history N` concatenates the last N observations into the state, which is the
cheap answer to a partially observed environment when the policy has no memory of
its own (`ObservationStack.cs`, [`RL_ARCHITECTURE.md`](RL_ARCHITECTURE.md) §4).
The first decision of an episode fills the window with its own frame rather than
with zeros, so a fresh round does not look like a world in which nothing moves.

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
learned. It is a worse bet than it was: the barracks now defends the ~100 m around
itself ([`NETCODE.md`](NETCODE.md) §10.4), so the last hundred metres of progress the
term pays for end in a death, and a policy has to learn the ring from the death
penalty alone. Measuring progress to the edge of the ring rather than to the door is
the obvious change; it has not been made.

## 7. The strategist seat

Same three files, different chair:
`StrategistActionSpace.cs` · `StrategistReward.cs` · `GdpyrStrategistEnv.cs`.

```bash
./scripts/train.sh --policy strategist --steps 100000 --save runs/strategist
```

### Observation — 1,092 floats

Exactly what a human strategist's client is sent: round state, 64 own units, 4
barracks, 8 resource nodes and 16 ground-force contacts, behind the same
`VisibilityService` a person is behind — live records where a sensor has one now,
ghosts with an age where one was seen and lost (`AGENT_API.md` §6.2). The optional
`32 × 32 × 4` feature planes (§6.3) ride in the same frame when a session asks for
them; the shipped environment does not, because RLMatrix's feed-forward agents
take a flat vector and a convolutional policy is a change of learner rather than
a flag.

### Action — six heads of eight, at most two commands a decision

**This is the research decision M7 deliberately did not make, made.** The game's
strategist action is an unbounded list of commands naming arbitrary unit ids and
arbitrary points; something has to choose which slice of it a discrete policy may
reach, and `StrategistActionSpace.cs` is that choice in one readable file:

| Head | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 |
|---|---|---|---|---|---|---|---|---|
| production | none | build infantry | build technical | build tank | cancel | rally to cell | rally to node | rally home |
| barracks | which door, folded onto the ones this seat owns |
| group | none | all | idle | near cell | far from cell | damaged | armor | infantry |
| order | none | move | attack | patrol | defend | stop | attack nearest contact | defend nearest node |
| target_x | the column of an 8 × 8 grid over the map |
| target_z | the row |

Three choices in there worth arguing with:

- **Two commands a decision, not sixty-four.** At 2 Hz that is four commands a
  second against an APM cap of eight (§7.3), and one decision stays legible in a
  trace. A policy that wants to move three groups separately spends three
  decisions on it, which is what a person does anyway.
- **Units are addressed by predicate, not by id.** Learning "unit 47" is learning
  a number that means something else next round. The groups are the categories a
  person plays with.
- **The grid is fitted to the map.** The encoder normalizes positions against a
  256 m half-extent and a greybox map is far smaller, so the grid is fitted to the
  bounding box of the resource nodes and this seat's barracks — map geometry,
  always visible, and it does not move during a round.

**Unit ids are not in the observation, and an order names them.** That join is
`list_units` (`AGENT_API.md` §7.5), one round trip per decision, and it returns
the army in exactly the order the observation's unit block carries it. The
alternative — a client-side roster inferred from `unit_built` and `unit_lost` —
drifts the first time a corpse outlives its event, and silently.

### Reward — `tools/Gdpyr.AgentClient/StrategistReward.cs`

| Term | Default | |
|---|---|---|
| ticket progress | `+6` per normalized unit of the enemy pool emptied | the dense term, and it *is* the win condition |
| income | `+1` per normalized unit paid | nodes held over time, so minute three can be credited before minute nineteen |
| damage dealt / taken | `+0.01` / `−0.005` per point | by side: every unit on the field is this seat's |
| unit lost | `−0.25` | points already spent |
| node gained / lost | `+1` / `−1` | |
| step cost | `−0.005` | a strategist that idles loses on the clock |
| round won / lost | `+10` / `−10` | outcome 1 is its win; 2 and 3 are the ground force's |

Same status as the ground seat's: every number is a hypothesis, in one file, and
nothing in it is a fact about gdpyr.

### Differences that leak into the loop

- **A command list is consumed, not held.** A ground action repeats for the seat's
  `step_mul` because that is what a held fire button is; holding a command list
  for thirty ticks would queue thirty riflemen (§7.4). One decision is one list.
- **An empty list is a decision**, not silence: it keeps the lease, and only a
  seat that stops submitting falls back to `BotStrategist` (§2.1).
- **The APM cap is what bites**, not the socket. Commands past the budget are
  dropped for that decision and counted in `AgentCommandBudget.Refused`.

## 8. Self-play and co-training

Two learners, one game. Read [`RL_ARCHITECTURE.md`](RL_ARCHITECTURE.md) §7 first:
**simultaneous self-play is the last step, not the first**, and the useful
version of it is training against frozen checkpoints.

```bash
./scripts/train-selfplay.sh --freeze ground --load-ground runs/ppo-ground
./scripts/train-selfplay.sh --steps 200000 --out runs/selfplay      # both learning
```

The mechanics the script exists to get right:

- **Stepped mode makes two learners a barrier, not a race.** The simulation holds
  while *any* stepped session has no ticks outstanding (`AGENT_API.md` §5.2), so a
  ground learner at 15 Hz and a strategist at 2 Hz interleave correctly without
  either knowing about the other.
- **`reset` ends the round for everybody**, so exactly one process may own the
  episode boundary. The script gives it to the strategist, whose episode is a
  whole round, and starts the ground learner with `--no-reset`: it takes the new
  round off its own observation a decision or two later instead of restarting one
  that has already restarted.
- **Raise `--step-timeout`.** The hold one learner is waiting through is the other
  one's optimizer step; at the 10-second default the slower of the two will
  eventually be dropped back to real time, and then it is learning from
  transitions a bot partly chose. The script uses 60 s.
- **Checkpoints land in `<out>/ground` and `<out>/strategist`**, metrics beside
  them. Keep every one: the set of frozen checkpoints is the league, and playing a
  new policy against the old ones is how you find out whether progress is real or
  a cycle.

## 9. Evaluating a checkpoint

```bash
./scripts/evaluate.sh runs/ppo-ground --episodes 20
./scripts/evaluate.sh runs/strategist --policy strategist --episodes 10
```

That plays the policy with learning switched off and stops after a number of
finished *rounds* rather than a number of decisions, because "how often does this
win" is a question about rounds. The last line is the one to write down:

```
ground: 20 episodes, 17 decided, 11 won, 6 lost, win rate 64.7%
```

Four rules, argued in [`RL_ARCHITECTURE.md`](RL_ARCHITECTURE.md) §6: a truncated
episode has no outcome and is in neither column; claims about this environment are
distributional because an episode is not reproducible (§10); the shaping term is
the first thing to ablate; and a result obtained under `--agent-omniscient` is a
result about a different game.

## 10. Determinism: measure it before you rely on it

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

## 11. Security

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

## 12. Writing your own client

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

There is a Gymnasium-shaped Python client in [`../tools/gdpyr_env/`](../tools/gdpyr_env/)
— `numpy` and nothing else — which speaks both seats, including `list_units`. It
is there because a great deal of RL tooling is Python, not because the observation
layout lives in two places: it is fetched at the handshake and decoded by name.

## 13. Playtests, which are not training

A scenario file plays a scripted round against a headless server and asserts
something about the result, with no display and nobody watching:
`./scripts/playtest.sh Tests/Scenarios/rifle_lethality.json`. It shares this
socket and nothing else — no libtorch, no learner, and a deterministic exit code
([`AGENT_API.md`](AGENT_API.md) §9). If a training run stops learning, that is
the first place to check whether the *game* changed under it.
