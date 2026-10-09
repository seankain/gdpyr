# gdpyr

Greybox prototype of an asymmetric 6v2 FPS-vs-RTS round. Godot 4.6, C# (`net8.0`),
dedicated-server authoritative.

- [`docs/IMPLEMENTATION_PLAN.md`](docs/IMPLEMENTATION_PLAN.md) — scope, architecture, milestones
- [`docs/NETCODE.md`](docs/NETCODE.md) — tick model, message set, ballistics, fog of war
- [`docs/AGENT_API.md`](docs/AGENT_API.md) — headless play for external policies: RL agents on
  either side, and scripted playtests for coding agents
- [`docs/TRAINING.md`](docs/TRAINING.md) — training a policy for either seat against a headless
  server, in C#, with [RLMatrix](https://github.com/asieradzk/RL_Matrix)
- [`docs/RL_ARCHITECTURE.md`](docs/RL_ARCHITECTURE.md) — which algorithm to reach for in 2026, what
  changed since PPO, and the order to run things in here
- [`docs/HTN_BOTS.md`](docs/HTN_BOTS.md) — hierarchical task network planners for the ground
  bots, the RTS units and the computer strategist, on FluidHTN — what it is, what it was checked to
  do, what it costs, and the phases; H0–H5 are built, and since H6 the HTN bots are the default,
  with scenarios of their own and the legacy bots pinned for training
- [`docs/AI_DEBUG.md`](docs/AI_DEBUG.md) — spectating, and the AI debugger: every bot's, unit's
  and commander squad's HTN with the running, planned and paused tasks marked on it, as text, a
  graph and lines in the world
- [`docs/DEMOS.md`](docs/DEMOS.md) — recording a round and watching it back: the console, the
  journal, and what playback does with it
- [`docs/LAN.md`](docs/LAN.md) — hosting a room of machines from a checkout, with a dev build
- [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) — AWS EC2 dedicated-server runbook

## Layout

```
Scripts/Core/   Bootstrap (CLI args, engine settings), LaunchOptions, weapon and unit catalogs
Scripts/Net/    TransportFactory, NetworkManager (transport + clocks), PlayerManager (tick loop, roster)
Scripts/Sim/    Engine-free simulation code; unit-tested without Godot
Scripts/Sim/Demo/ The demo container: header, records, writer, reader — a codec like the others
Scripts/Sim/Htn/ HTN planning: the pooled FluidHTN factory, the contact memories, squad and zone
                boards and force ratio, the ground bot's domain and its coordinator (H2),
                the unit domain and its squad census (H3), the strategist's commander (H4)
Scripts/Sim/AiDebug/ The AI debugger's engine-free half: domain maps, plan traces and history,
                the text and graph renderers, and the spectator's wire format
Scripts/Fps/    Character controller, movement FSM, input sampler, weapons, viewmodel
Scripts/Rts/    Units, barracks, orders, the strategist camera and selection
Scripts/Match/  CombatManager (the round), MatchState, TeamService
Scripts/Bots/   Computer players: the roster director, the ground pilot, the strategist, the sensor
Scripts/Agent/  The agent control channel: listener, sessions, seats, observations, feature planes
Scripts/Ui/     Main menu and server browser, role select, pause menu, reticle, combat HUD, RTS HUD,
                net debug HUD, the `~` console and the demo camera
Scripts/Ui/Spectator/ The spectator's camera, and the AI debugger's panel, graph and world overlay
Scenes/         Main menu, greybox map, player, weapon, pause menu
Units/          UnitDefinition resources: three fighting tiers, the builder and the supply truck
Structures/     StructureDefinition resources: pillbox, sandbag wall, sniper tower
Tests/          xUnit over the engine-free sources — `dotnet test`, no Godot needed
Tests/Scenarios/ Playtest scenario files: data, and authoring one needs no Godot install
ThirdParty/     Vendored source: FluidHTN at a pinned commit (docs/HTN_BOTS.md §3.8)
tools/          External processes that talk to a server over a socket: the agent client (both
                seats' action spaces, rewards and environments), the RLMatrix trainer, the
                divergence probe, the playtest harness, a Python client, and the HTN planner
                probes and benchmark (docs/HTN_BOTS.md §3). Never part of the
                game assembly (docs/TRAINING.md)
```

The simulation runs in `_PhysicsProcess` at a fixed 60 Hz and reads nothing but the recorded
`InputFrame` for the tick. `Scripts/Fps/LocalInputSampler.cs` is the only place the `Input`
singleton is read, and rendering-only work (viewmodel sway, the correction offset the camera is
drawn with) is the only thing left on the render frame.

That one property is what makes demos cheap: write the frames out where they are resolved and you
have written the round down ([`docs/DEMOS.md`](docs/DEMOS.md)).

## Running

Godot consumes its own arguments first, so the game's arguments go after a bare `--`.

| Command | Mode |
|---|---|
| `godot --path .` | the main menu (also what F5 in the editor does) |
| `godot --path . -- --listen [port]` | authority plus a local player, for solo testing |
| `godot --path . -- --client <host[:port]>` | connect to a server |
| `godot --path . --headless -- --server [port]` | headless authority, no local player |

Default port is 7777/UDP. A dedicated-server export with no mode flag defaults to
`--server` rather than to offline.

**The main menu** lists the servers on this network, takes an address for one anywhere else, and
hosts or runs a round on its own. A launch that named a mode on the command line goes straight past
it into the map, before a widget is built — so the headless server, the playtest harness and the
training runs are on the path they always were.

The list is LAN discovery and nothing else: each server answers a broadcast query on UDP 7780–7783
with its name, its game port and its roster, and a row that stops answering ages out
([`docs/LAN.md`](docs/LAN.md) §4). It reaches the room, not the internet — there is still no
matchmaking and none is planned. Two flags change what a host looks like in it:

| Flag | Effect |
|---|---|
| `--name <text>` | what this server calls itself in the list (default: the machine's hostname) |
| `--no-advertise` | answer no discovery queries; the address still works |

Hosting for other people on the same network — addresses, the one firewall rule, seats and bots,
and what a LAN hides — is [`docs/LAN.md`](docs/LAN.md); no export needed.

Three more flags change the computer players the authority keeps around:

| Flag | Effect |
|---|---|
| `--bots <n>` | fill the ground force to `n` players, humans included |
| `--bots <n>:<m>` | ...and the strategists to `m` |
| `--no-bots` | no computer players at all |
| `--bot-ai legacy\|htn` | what they decide with: the HTN planners of [`docs/HTN_BOTS.md`](docs/HTN_BOTS.md), the default, or the code from before them |

None is needed to get bots: the numbers default to `BotGroundForce` and `BotStrategists` in
`Match/default_gamemode.tres` (6 and 1), and the AI to its `BotAi` (htn since H6). `--bots 4`
overrides only the ground force and leaves the strategists to the game mode. The training and
evaluation scripts start their servers with `--bot-ai legacy` whatever the default is: the legacy
bots are the baseline a policy is measured against ([`docs/TRAINING.md`](docs/TRAINING.md) §3,
§9). Under `htn` the ground bots plan
with the HTN of [`docs/HTN_BOTS.md`](docs/HTN_BOTS.md) §5.1 (H2), the units with §5.2's (H3),
whoever ordered them, and the computer strategist with §5.3's commander (H4) — which, from H5,
buys a supply truck once it has eight fighting units, stations it behind its squads, and sends a
wounded squad, or a bot's wounded unit, to the nearest supply to heal. The debug HUD's
`ground plan` row counts the ground bots on each task and the roles their coordinator handed out,
`unit plan` the units on each task, `unit ms` what the unit pass costs a tick, `resupply` the units
healing and the health given back this round (under either AI), and `commander` each computer
strategist's squads and what each is doing.

Two more watch rather than play ([`docs/AI_DEBUG.md`](docs/AI_DEBUG.md)):

| Flag | Effect |
|---|---|
| `--spectate` | join as a spectator: no character, a free camera, and the AI debugger. Alone, an offline round of bots to watch |
| `--ai-debug`, `--no-ai-debug` | whether this authority sends its bots' plans to spectators; on except in a dedicated-server export |

Two more record a round or watch one back ([`docs/DEMOS.md`](docs/DEMOS.md)):

| Flag | Effect |
|---|---|
| `--record <name>` | write a demo from the round's first tick; how a headless server records |
| `--playdemo <name>` | watch a demo instead of playing; no mode flag may go beside it |

Four more open the agent control channel, which lets a process that is not a Godot client take a
bot's seat ([`docs/AGENT_API.md`](docs/AGENT_API.md)):

| Flag | Effect |
|---|---|
| `--agent-api [host:]port` | listen for external policies; a bare port binds loopback |
| `--agent-token <token>` | required for a non-loopback bind, which is otherwise a fatal start-up error |
| `--agent-unbounded` | lift the turn-rate and APM ceilings; research runs only |
| `--agent-omniscient` | drop the fog for attached seats, and label every observation as cheating |

The deploy unit never passes any of them: the socket can spawn players, issue orders and reset
rounds, and the box in [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) has a public address.

The server spawns a character per connected peer at the map's `player_spawn` markers; each client
predicts its own and interpolates everyone else.

## Playing either side

Every round opens on the same question: ground force, or strategist. The panel is up when you join
and again when a round starts, and the server holds you off the field until you answer — nobody is
spawned into a side they did not pick. Answering with `F1` / `F2` is the same answer as clicking;
the two keys keep working mid-round for changing your mind.

Two strategists at a time; a third request lands on the ground, and you find that out from the HUD
rather than from a second menu. A strategist's body leaves the field and the camera moves above the
map:

| | |
|---|---|
| WASD, or the screen edge | pan |
| Q / E, mouse wheel | rotate, zoom |
| left-drag, left-click, `F` | box select, single select, select all |
| right-click | order the selection (or, with nothing selected, move the barracks rally point) |
| `X` `C` `B` `Z` | attack-move · patrol · defend · stop, applied by the next right-click |
| `1` `2` `3` `4` `8`, Backspace | queue an infantryman, a technical, a tank, a builder or a supply truck; cancel the last one |
| `5` `6` `7` | with builders selected: put a pillbox, a sandbag wall or a sniper tower where the next right-click lands |

Right-clicking an enemy player orders an attack on that player rather than on the ground under
them. Units are server-simulated and never predicted; the order marker appears immediately and the
units move a round trip later, which is what an RTS feels like anyway.

**Builders** put up three structures ([`docs/NETCODE.md`](docs/NETCODE.md) §10.5). A **pillbox**
(150 points) is a concrete box with a gun in it that bullets do nothing to; a **sandbag wall** (25) is
waist-high cover and nothing else; a **sniper tower** (125) puts a marksman eight metres up who sees
and shoots to 90 m, and is as bullet-proof as the pillbox. A structure faces the way the camera
does, so a wall lies across the screen and `Q`/`E` turn it. It is paid for when it is placed, goes up
while builders stand beside it — two build twice as fast — and cannot finish while somebody from the
ground force is standing where it goes. Right-clicking builders onto one of your own sites or damaged
structures finishes or mends it. Finished, it is solid for everybody: it stops bullets, bodies and
sight lines, and a crouched player behind a sandbag wall is hidden from a rifleman in front of it.

**Units heal** ([`docs/HTN_BOTS.md`](docs/HTN_BOTS.md) §8, D2) inside a barracks' red ring and inside
the green ring round a **supply truck** (120 points, `8`), once nothing has hit them for three
seconds — a rifleman at half health is whole again ten seconds later. The truck is soft, carries a
pistol and does not heal itself; move it up behind a fight and send the wounded back to it.

On the ground, right mouse raises the sights. What that gets you is the weapon's:

| Weapon | Aiming |
|---|---|
| DMR | a four-power optic — the eyepiece takes the screen, the gun leaves it, and the view goes with it |
| rifle, launcher, pistol | the sights come to the middle of the screen, with a fraction of the zoom |
| hammer, a mounted heavy gun | nothing to raise; a mounted gun is aimed over its own sights |

Turn rate is divided by whatever magnification is in force, so a pixel of mouse travel is worth the
same distance on screen at every power — a four-power scope at the hip's sensitivity sweeps four
times as much of what you can see. The sights are a client's own: they are raised from the same
recorded input frame the rest of the simulation runs on, and nothing about them goes on the wire
([`docs/NETCODE.md`](docs/NETCODE.md) §4.7).

**Armour, and the weapon locker.** The tank, the pillbox and the sniper tower are armour: rifle, DMR,
pistol and heavy-gun rounds stop against them and do nothing, and only the launcher's grenade — by its
impact or its blast — hurts them. The answer when one turns up is the **weapon locker** at the spawn:
stand at it and tap `E` to swap the large weapon in your hands for the next one, with a full
magazine — rifle, launcher, DMR, and round again. The swap is for that life; what you spawn with is
still the loadout menu's (`1`/`2`/`3` while dead). A hit on armour that did nothing shows no hit
marker ([`docs/NETCODE.md`](docs/NETCODE.md) §10.6).

## Playing on your own

Computer players fill both sides so that one person is enough for a round. They arrive as soon as
anybody is connected and leave as people take their seats — a bot holds a slot only while nobody
else wants it, and an empty server runs no bots at all.

**Practice offline** in the main menu is the whole thing with no networking at all: the same
authoritative path, the same bots, every broadcast skipped for want of a peer.

A bot is a player: same peer id, same character, same loadout, same place in the snapshot, same
ticket when it dies. The only difference is that the server takes its input from a brain rather than
from a socket (`docs/NETCODE.md` §9), so a client cannot tell one from a person and no part of the
netcode had to learn about them. The one thing they are never asked is which side they want — the
director assigns that on the tick they join, so the role menu is a question for people only and a
round full of bots starts without waiting for anybody.

- **On the ground** they walk towards the enemy barracks and wait at the edge of its defences,
  engage the nearest unit they can see, close to about 25 m and then strafe, and hold fire when
  somebody on their side is in the way.
  They are deliberately mediocre shots — a 3° aim error held for a third of a second at a time, and
  a 200 ms reaction. `BotTraits.Default` in `Scripts/Sim/BotBrain.cs` is the whole difficulty dial.
  Six carry two rifles, two DMRs and two launchers. Only the launchers take on a tank, a pillbox or a
  tower, and they hold fire while the blast would reach a teammate or themselves.
- **In the strategist's chair** one queues infantry at every barracks while the points last, keeps
  four units home defending, and attack-moves the rest at whatever its units have actually seen —
  it gets no free knowledge of where anybody is. Once it has four fighting units it buys a builder,
  and fortifies the resource nodes with a pillbox, a wall in front of it and a tower behind, facing
  the ground force's spawn. Ground bots shoot a pillbox or a tower only when no unit is in sight.

The debug HUD below counts them: `bots: 5/6 ground  1/1 strategist` is five ground bots against a
target of six, and one strategist bot against a target of one.

## Watching the bots think

**Watch the bots** on the main menu — or `--spectate`, or **Spectate (F3)** on the role menu, or
`F3` mid-round — takes you off the field with no character and a free camera (right-drag to look,
WASD to fly). Bots fill both sides for a spectator as they would for a player. Click a bot or a
unit, and the panel shows its HTN: the task running (`>>`), the tasks planned behind it (`#1`,
`#2`), those behind a pause (`||`), which conditions hold now (`+`) and which do not (`-`), its
facts, and the last sixteen things its planner did and why; in the world, a line to every place
its plan knows about. `F6` does the same for the computer strategist's commander, squad by squad;
`F5` labels every agent's task at once; `F7` draws the tree as a graph. On the console,
`ai_plan bot 3`, `ai_squads` and `ai_tree ground` print the same as text. The whole of it, and what
a server sends a spectator, is [`docs/AI_DEBUG.md`](docs/AI_DEBUG.md).

Press `` ` `` on a client for the net debug HUD — RTT, clock lead, input buffer depth, mispredictions
per second, prediction error, bytes in/out, server frame time. A headless server has no HUD and logs
a line every five seconds instead:

```
[net] tick 1800 | peers 3 | in 9.2 KB/s | out 22.1 KB/s
```

How to read those numbers, and how to inject latency with `tc netem` to test against something other
than a perfect link, is in [`docs/DEPLOYMENT.md`](docs/DEPLOYMENT.md) §4.

## Watching a round back

Press `~` — shift and the backtick — for the console. It is there at the main menu as well as in the
map, because `playdemo` is asked for before there is a round; `` ` `` on its own is still the net
debug HUD above.

```
record game.demo        start writing a demo of this round
stoprecord              close it
mark <text>             put a note in the demo at this tick
playdemo game.demo      watch one
demos, demoinfo <name>  what is in the folder, and what is in one
demospeed, demopause, demofollow <peer>
```

A demo is the round's **input journal** — every character's `InputFrame`, every tick, as the
authority resolved it — plus the snapshots it broadcast. Playback re-runs the movement step over the
journal at the full 60 Hz and lets the snapshots correct it, which is why a replay moves the way the
round did rather than the way a 30 Hz recording of it would. Journalling is also the cheap half of
the file: eight players of intent is 8.4 KB/s, against 24 KB/s for the keyframes beside it.

A client can record too. It is never told what anybody else pressed, so what it writes is its own
input and the packets it was sent, and that plays back by interpolation — a Source-style demo rather
than a journal. Neither is chosen: `record` writes the kind this process can.

| Flag | Effect |
|---|---|
| `--record <name>` | record from the round's first tick; how a headless server records |
| `--playdemo <name>` | go straight past the menu into a replay |

The whole of it — the format, the cost, the camera, and what this first cut deliberately leaves out
— is [`docs/DEMOS.md`](docs/DEMOS.md).

## Building

```bash
dotnet build Gdpyr.csproj     # game assembly (needs no Godot install)
dotnet test                   # Tests/Gdpyr.Tests.csproj
./scripts/export-server.sh    # headless Linux server -> build/server/
./scripts/deploy.sh           # rsync to EC2 + restart the systemd unit
```

## Training agents

A headless server plus an out-of-band socket is enough to put a reinforcement-learning policy in
either seat — the ground force or the strategist's chair. The learner is C# —
[RLMatrix](https://github.com/asieradzk/RL_Matrix) on TorchSharp — and lives outside the game
assembly:

```bash
./scripts/train.sh                                 # one server, PPO, a ground seat
./scripts/train.sh --policy strategist             # the other chair
./scripts/train.sh --ports 7900,7901 --history 4   # two servers, one learner, stacked frames
./scripts/evaluate.sh runs/ppo-ground --episodes 20    # win rate against the scripted bots
./scripts/train-selfplay.sh --freeze ground --load-ground runs/ppo-ground
./scripts/divergence.sh --ticks 600                # how reproducible is a seeded episode? (§3.1)
```

Each seat is three files you are meant to disagree with: an action space (what the policy may do), a
reward (what it is being asked to want) and an environment. The game itself emits events and no
rewards, on purpose. [`docs/TRAINING.md`](docs/TRAINING.md) has the whole story — including why you
should train in stepped mode, how two learners share one server, and the large libtorch download
RLMatrix's packaging makes unavoidable — and
[`docs/RL_ARCHITECTURE.md`](docs/RL_ARCHITECTURE.md) answers the question that comes first: PPO is
still the default, a modern DQN is Rainbow and is the sample-efficient alternative worth measuring
against it here, and what actually changed in the last decade is everything around the algorithm.

There is a Gymnasium-shaped Python client in [`tools/gdpyr_env/`](tools/gdpyr_env/) for the rest of
the RL ecosystem — `numpy` and nothing else.

## Playtests without people

The same socket answers "did my change break the game" with no display and nobody in a voice call.
A scenario is a JSON file — a seed, a roster, what to put on the map, what to script, and what to
claim about the result — and running one is an exit code:

```bash
./scripts/playtest.sh Tests/Scenarios/rifle_lethality.json
./scripts/playtest.sh Tests/Scenarios/*.json --json   # machine-readable, for CI
```

Exit 0 when every claim held, 1 when one failed, 2 when the harness could not run it. A failure
names the tick it failed on and a `trace-<seed>.jsonl` that replays. The assertion vocabulary is
distributional on purpose — `between`, `percentile`, `over_seeds`, and deliberately nothing that
names a tick — because this is a stochastic environment with a seeded core and a suite that asserts
trajectories is a suite that flakes ([`docs/AGENT_API.md`](docs/AGENT_API.md) §3, §9).

The `htn_*` scenarios make claims about the HTN bots and say so (`"bot_ai": "htn"`); the harness
starts their server with them, and `--bot-ai legacy` on the command line runs the same claims
against the legacy bots, which fail them ([`docs/HTN_BOTS.md`](docs/HTN_BOTS.md) §6, H6).
