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
- [`docs/COVER.md`](docs/COVER.md) — cover: the low walls and blocks on the map, where the HTN
  bots and units stand to fight from behind them, aiming at what shows over a wall, and the
  computer strategist's builder laying sandbags in front of the squads it keeps holding ground
- [`docs/TACTICS.md`](docs/TACTICS.md) — unit tactics without micro — taking cover from
  the shooter, gunners suppressing for a rifle assault, snipers firing from concealment, grenades
  behind cover, a move to a control point that fights its way there, units holding ground covering
  an exposed builder while it builds, and a per-unit switch the strategist turns it off with; T0–T9
  are built, on by default since T8, with what bot-only rounds measured on and off
- [`docs/AI_DEBUG.md`](docs/AI_DEBUG.md) — spectating, and the AI debugger: every bot's, unit's
  and commander squad's HTN with the running, planned and paused tasks marked on it, as text, a
  graph and lines in the world
- [`docs/DEMOS.md`](docs/DEMOS.md) — recording a round and watching it back: the console, the
  journal, and what playback does with it
- [`docs/VEHICLES.md`](docs/VEHICLES.md) — vehicles that carry units: the technical with seats,
  boarding and unloading, and a vertical take-off and landing dropship
- [`docs/LOADOUT.md`](docs/LOADOUT.md) — the ground force's loadout: a locker panel for
  the primary, spare magazines or grenades and body armour, weight that sets movement speed, and
  resupply at the locker; L0–L8, all built — how much bots fire in a life,
  which raised the default kit to five spare magazines; the kit itself: finite primary
  ammunition, no primary at all, and the player record that carries both; taking a kit at the
  locker, one validator for a person, a bot and an agent; body armour, a vest that takes a
  share of each hit until its points run out; weight, which sets how fast a kit moves;
  resupply within a metre of the locker, paced, and not under fire; the locker's panel on
  `E`, which is also the death screen's editor of the next spawn's kit; bots that rearm at the
  locker when they run dry, in kits for their roles; and the kit in the agent observation's
  vector, `gdpyr-agent-obs-4`
- [`docs/CONTROL_POINTS.md`](docs/CONTROL_POINTS.md) — what the resource nodes do besides pay:
  a node the strategist holds is a door for its barracks (forward deployment, which the computer
  strategist uses), and a node can carry a perk for the ground force — the FPV drone, flown by
  looking, which kills a tank on a direct hit; what bot-only rounds measured, and what is left for
  a person to playtest
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
Scripts/Characters/ The humanoid body players and units on foot are drawn as: the rig whose
                AnimationTree poses the Universal Animation Library's mannequin in layers —
                the legs' stride, the arms' aim, recoil and throw over it, a death over
                both; how it is posed is Scripts/Sim/BodyAnimation.cs, engine-free
Scripts/Rts/    Units, barracks, orders, the strategist camera and selection
Scripts/Match/  CombatManager (the round), MatchState, TeamService
Scripts/Bots/   Computer players: the roster director, the ground pilot, the strategist, the sensor
Scripts/Agent/  The agent control channel: listener, sessions, seats, observations, feature planes
Scripts/Ui/     Main menu and server browser, role select, pause menu, reticle, combat HUD, RTS HUD,
                net debug HUD, the `~` console and the demo camera
Scripts/Ui/Spectator/ The spectator's camera, and the AI debugger's panel, graph and world overlay
Scenes/         Main menu, greybox map, player, humanoid body, weapon, pause menu
Units/          UnitDefinition resources: three fighting tiers, the builder, the supply truck, and the
                sniper and heavy gunner a sniper tower and a pillbox train
Structures/     StructureDefinition resources: pillbox, sandbag wall, sniper tower
Tests/          xUnit over the engine-free sources — `dotnet test`, no Godot needed
Tests/Scenarios/ Playtest scenario files: data, and authoring one needs no Godot install
ThirdParty/     Vendored source: FluidHTN at a pinned commit (docs/HTN_BOTS.md §3.8); and assets: the
                Universal Animation Library's mannequin and clips (Quaternius, CC0), and the
                prototyping models and textures
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

Four more flags change the computer players the authority keeps around, and how the units fight:

| Flag | Effect |
|---|---|
| `--bots <n>` | fill the ground force to `n` players, humans included |
| `--bots <n>:<m>` | ...and the strategists to `m` |
| `--no-bots` | no computer players at all |
| `--bot-ai legacy\|htn` | what they decide with: the HTN planners of [`docs/HTN_BOTS.md`](docs/HTN_BOTS.md), the default, or the code from before them |
| `--bot-kits default\|role` | what HTN ground bots carry besides their roster slot's primary ([`docs/LOADOUT.md`](docs/LOADOUT.md) §8): a kit for the primary's role, the default since L7 — rifle 4 magazines, 2 grenades, light vest; DMR 5, 1, none; launcher 3 pouches, 3 grenades, medium vest — or the default kit every bot carried before. Legacy bots carry the default kit either way, and so does a policy's seat |
| `--unit-autonomy on\|off` | whether the strategist side's new units use tactics on their own ([`docs/TACTICS.md`](docs/TACTICS.md) §5.9); on by default since T8, and a strategist changes it in game; `off` is how units fought before the tactics |

None is needed to get bots: the numbers default to `BotGroundForce` and `BotStrategists` in
`Match/default_gamemode.tres` (6 and 1), the AI to its `BotAi` (htn since H6), the bots' kits to
its `BotKits` (role since LOADOUT L7), and the units' micro-autonomy to its `UnitAutonomy` (on
since TACTICS T8). `--bots 4`
overrides only the ground force and leaves the strategists to the game mode. The training and
evaluation scripts start their servers with `--bot-ai legacy` whatever the default is: the legacy
bots are the baseline a policy is measured against ([`docs/TRAINING.md`](docs/TRAINING.md) §3,
§9). Under `htn` the ground bots plan
with the HTN of [`docs/HTN_BOTS.md`](docs/HTN_BOTS.md) §5.1 (H2), the units with §5.2's (H3),
whoever ordered them, and the computer strategist with §5.3's commander (H4) — which, from H5,
buys a supply truck once it has eight fighting units, stations it behind its squads, and sends a
wounded squad, or a bot's wounded unit, to the nearest supply to heal; and, from T7
([`docs/TACTICS.md`](docs/TACTICS.md) §6), trains a heavy gunner at its pillboxes for every three
riflemen past its garrison, one to each assault squad, and two snipers at its towers, which join
the garrison or a squad holding a zone. Every unit on foot with a doctrine — a rifleman, a heavy
gunner, a sniper — uses the tactics of [`docs/TACTICS.md`](docs/TACTICS.md) §5 while its
micro-autonomy is on, the default since T8, whoever ordered it; from T9 that includes a rifleman or
heavy gunner holding ground going to cover a builder fired upon at its site nearby (§5.10). The debug HUD's
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
map.

The HUD keeps text to a minimum: three short lines top left (points, units and clock; what the
barracks is building; the nodes) and, bottom right, a **command card** for whatever is selected,
each button with its key printed on it:

| selected | the card |
|---|---|
| the barracks | a button per unit it trains, the queue, **Cancel last**; right-click the ground sets its rally point |
| a finished sniper tower or pillbox | its one unit, its queue, **Cancel last** |
| nothing but builders | a button per structure — **Pillbox (5)**, **Sandbag wall (6)**, **Sniper tower (7)** — with its price; the armed one stays lit until it is placed, or pressed again; and the orders |
| a technical or a dropship, among anything | who is aboard of how many seats, the orders, **Unload (U)**, and **Tactics (H)** if units on foot are selected too |
| any other units | the orders — **Attack-move (X)**, **Patrol (C)**, **Defend (B)**, **Stop (Z)**, the armed one lit — and **Tactics (H)** for units that have tactics |

Every key below works whether or not the card is up. `K`, or the **Keys** button bottom left, opens
and folds away the full key list:

| | |
|---|---|
| WASD, or the screen edge | pan |
| Q / E, mouse wheel | rotate, zoom |
| left-drag, left-click, `F` | box select, single select, select all |
| right-click | order the selection (or, with nothing selected, move the barracks rally point — onto a node you hold, and the barracks' units come out there) |
| `X` `C` `B` `Z` | attack-move · patrol · defend · stop, applied by the next right-click |
| `1` `2` `3` `4` `8`, Backspace | queue an infantryman, a technical, a tank, a builder or a supply truck; cancel the last one |
| `5` `6` `7` | with builders selected: put a pillbox, a sandbag wall or a sniper tower where the next right-click lands |
| `9` `0` | queue a sniper at a sniper tower, a heavy gunner at a pillbox (the selected one, else the one with the shortest queue) |
| `T` | queue a dropship at the barracks |
| right-click a technical or a dropship, with units selected | they walk to it and get in, nearest first, up to its seats (four, or eight); the vehicle becomes the selection and waits for them — a dropship in the air comes down to be boarded |
| `U` | every selected vehicle with anybody aboard lets them out round it, where it is; a dropship lands first |
| `H`, or the card's **Tactics** button | the selection's tactics ([`docs/TACTICS.md`](docs/TACTICS.md) §5.9). **Auto**, the default: units take cover from where they are fired on and fire back, throw grenades at enemies the side knows are behind cover, heavy gunners suppress where enemies are or were, snipers fire from concealment, pop out and move on, and a move onto a resource node is taken as a squad — to ground, a base of fire, bounds from cover, grenades, the rush — rather than walked into fire. **Direct**: they do what they are told, as before the tactics. The **new units** button beside the points sets what the side's next units start with |
| `K`, or the **Keys** button | show or hide the full key list |

Right-clicking an enemy player orders an attack on that player rather than on the ground under
them. Units are server-simulated and never predicted; the order marker appears immediately and the
units move a round trip later, which is what an RTS feels like anyway.

**Control points** ([`docs/CONTROL_POINTS.md`](docs/CONTROL_POINTS.md)) are doors as well as
income. Put the barracks' rally point on the pad of a node you hold and what it trains comes out at
the node instead of at the barracks — every unit but the dropship — while nobody from the ground
force is standing on that pad; the production line says `· out at the held node` while it does. The
computer strategist does the same with a quiet node towards the ground force once its home garrison
is full, and keeps the squads it is forming there.

**Builders** put up three structures ([`docs/NETCODE.md`](docs/NETCODE.md) §10.5). A **pillbox**
(150 points) is a concrete box with a gun in it that bullets do nothing to; a **sandbag wall** (25) is
waist-high cover and nothing else; a **sniper tower** (125) puts a marksman eight metres up who sees
and shoots to 90 m, and is as bullet-proof as the pillbox. A structure faces the way the camera
does, so a wall lies across the screen and `Q`/`E` turn it. It is paid for when it is placed, goes up
while builders stand beside it — two build twice as fast — and cannot finish while somebody from the
ground force is standing where it goes. Right-clicking builders onto one of your own sites or damaged
structures finishes or mends it. Finished, it is solid for everybody: it stops bullets, bodies and
sight lines, and a crouched player behind a sandbag wall is hidden from a rifleman in front of it.

**Towers and pillboxes train units** ([`docs/NETCODE.md`](docs/NETCODE.md) §10.8). Click a finished
one and the card is its queue, as a barracks' is; a right-click on the ground with no units selected
moves its rally point. A **sniper tower** trains **snipers** (60 points, `9`): the tower's marksman on
foot, shooting as far as it sees, 70 m, two hits to a kill, on a body softer than a rifleman's. A
**pillbox** trains **heavy gunners** (90 points, `0`): slow, tougher, with a 150-round belt, and they
fire suppressively — a gunner that loses sight of a target keeps firing for four seconds at the
place it last saw it, so a player who ducks behind a wall finds the wall still being shot at when they
look up. Neither comes out of a barracks. A knocked-down structure refunds what was still on its queue.

**Cover** ([`docs/COVER.md`](docs/COVER.md)): the map has 18 low walls, 1.2 m high, that a
standing body fires over and a crouched one hides behind, and 8 blocks nothing sees through. A
finished sandbag wall is cover in the same way. A map marks its cover by putting `CSGBox3D` nodes
in the `cover` group.

**The technical carries four** ([`docs/VEHICLES.md`](docs/VEHICLES.md)). Select units on foot and
right-click a technical: the nearest four walk to it and get in, the rest stop where they are, and the
truck holds until they are aboard — a right-click on the ground in the meantime is where it drives
once they are. Pips over the truck show who is aboard. Passengers do not shoot, see, hold a node or
heal, and nothing can hit them; select the truck and press `U` (or the card's **Unload**) and they
get out round it. A truck destroyed with them aboard throws them out, each losing half its health.

**The dropship carries eight** (250 points, `T`). It takes off when it has somewhere to go, flies a
straight line at 14 m over everything, and lands whenever it has nowhere to go — on arriving, on
`Z`, and to be boarded or unloaded; a ring on the ground shows where it is over, red when it has found
no room to land. It is unarmed, so attack-move and defend are only somewhere to fly, sees 40 m from
up there over every wall, and bullets do half to it. Destroyed within 4 m of the ground it throws its
riders out as the truck does; higher than that, nobody aboard lives.

**Units heal** ([`docs/HTN_BOTS.md`](docs/HTN_BOTS.md) §8, D2) inside a barracks' red ring and inside
the green ring round a **supply truck** (120 points, `8`), once nothing has hit them for three
seconds — a rifleman at half health is whole again ten seconds later. The truck is soft, carries a
pistol and does not heal itself; move it up behind a fight and send the wounded back to it.

**Hand grenades** ([`docs/NETCODE.md`](docs/NETCODE.md) §10.9): three a life, thrown with `G`
whatever is in your hands, 10° above where you are looking — 17 m looking level, 33 m looking 35° up.
They go off three seconds after the throw, lying where they landed; anything that would stop a
bullet stops one, and one that hits somebody drops at their feet. They are anti-personnel: a blast
that kills within 2 m and reaches 7 m, and a quarter of that to a vehicle or a structure. The blast
hurts you and your teammates too. Strategist units on foot carry three as well and throw them at a
player who is behind a wall or standing still 6–28 m away; the technical, the tank, the supply
truck and the dropship carry none.

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
impact or its blast — hurts them properly; a hand grenade does a quarter of its blast. The dropship
is not armour, but its airframe takes half of what a bullet does. The answer when one turns up is the **weapon locker** at the spawn:
stand within a metre of the cabinet and press `E` (the prompt reads `[E] weapon locker`), and its
panel opens: the primary — none, rifle, DMR or launcher — nine pouches shared between spare
magazines and up to three grenades, and no vest or a light, medium or heavy one, with what the kit
weighs, how fast it walks and sprints, what a full one carries and how many rifle hits it stands.
You stand still and the mouse is free while it is up. A different primary comes with a full
magazine and its pouches full; what you already carried keeps what is left in it, and a pouch
added is filled by standing there. The kit is also the next spawn's. On the death screen, under
`down — respawning`, a line says what the next spawn carries, and `E` opens the same panel for
that kit alone (`loadout` on the `~` console sets it too). A hit on armour that did nothing shows
no hit marker ([`docs/NETCODE.md`](docs/NETCODE.md) §10.6).

| In the locker's panel | |
|---|---|
| `1` `2` `3` `4` | no primary, rifle, DMR, launcher |
| `5` `6` · `7` `8` | a magazine fewer or more · a grenade fewer or more |
| `9` `0` | a lighter or a heavier vest |
| Enter | take the kit — on the death screen, keep it for the next spawn |
| Backspace · `D` | back to what you carry · the default kit |
| `E`, Escape | close, taking nothing; Escape does not open the pause menu while the panel is up |

The mouse does the same on the panel's buttons, and the console still opens and types over it.
A kit the server refuses says why at the panel's foot (`refused (not_at_locker): too far from the
locker`) and leaves it open; a kit taken closes it, and so do dying, changing side, the role menu,
the end of the round and leaving the locker's reach
([`docs/LOADOUT.md`](docs/LOADOUT.md) §7.2).

A kit's last word is **body armour**, for players: a light, medium or heavy vest takes 35, 50 or
65 % of each bullet or swing, and half that of a blast, until its 35, 60 or 100 points are spent
— a heavy vest stands ten rifle hits where no vest stands five. The HUD shows what is left beside
health (`HP 100   vest 60   grenades 3`), other players wear theirs as a band round the chest, light
to heavy from khaki to charcoal, and asking a locker for the vest you already wear does not
refill it ([`docs/LOADOUT.md`](docs/LOADOUT.md) §4.3) — standing at it does.

**FPV drones** ([`docs/CONTROL_POINTS.md`](docs/CONTROL_POINTS.md) §4). The middle node and the far
one are drone nodes — a post with a ring on it at the middle of the pad, lit while your side holds
it. Each one your side holds builds a drone in two minutes — both together, in one — and the side
keeps one at a time; `Q` launches it while your side still holds a drone node. You see out of its nose and fly it by
looking — it goes 24 m/s and turns towards wherever you look — while your body stands where you
left it. It goes off on whatever it hits, on `fire` once it has flown half a second, or when its
15 s battery runs out: 800 to what it hits, which is a dead tank, and a 4 m blast for everything
near. If you die, it falls and does nothing. The line under the round's clock says what you have
(`[Q] drone`, `drone 40 %`, `drone 12 s · fire sets it off`).

**Resupply.** Stand within a metre of the locker's cabinet — inside the green line on the floor
round it — and it tops your kit back up: a pouch's rounds every half second, a grenade every
second and thirty vest points a second, up to what the kit holds, but not health and not the
magazine in the weapon, and nothing at all for three seconds after anything hurts you. The prompt
under the reticle says what it is doing (`resupplying — 4/5 mags · 2/3 grenades · 40/60 vest`, or
`resupply waits: under fire`) ([`docs/LOADOUT.md`](docs/LOADOUT.md) L-D7).

Everything in a kit **weighs** something, and the weight sets how fast its carrier moves: the
default kit — a rifle, five spare magazines, three grenades, no vest, 9.3 kg — walks at 5 and
sprints at 8 m/s, as every player always has; each kilogram over it costs 2 % of every movement
speed and each one under it gains 2 %, from a pistol alone at 112 % (sprinting at 9.0 m/s) down to
70 % for a launcher, nine pouches and a heavy vest, whose 5.6 m/s sprint is too slow to slide. A
jump is as high in any kit, and weight is the kit as taken, not as spent: firing a magazine away
makes nobody faster. The HUD's second line reads `load 15.3 kg · 88 %`
([`docs/LOADOUT.md`](docs/LOADOUT.md) §4.1).

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
  tower, and they hold fire while the blast would reach a teammate or themselves. Every bot throws
  its grenades at a unit on foot that is behind a wall or standing still 9–28 m away, under the same
  check for teammates. Under the HTN
  bots, one with a low wall within 25 m of a fight walks to the side of it away from the enemy,
  fights from there, and crouches behind it to reload. A unit whose chest is behind a wall is
  shot at by its head.
  An HTN bot with a drone on its side's shelf puts it up at a strategist tank it can see 15–140 m
  away, with nothing on foot within 20 m of it, and flies it into the hull; the legacy bots do not.
  Their ammunition runs out as anyone's does: a bot whose rifle, DMR or launcher is dry fights on
  with the pistol, and under the HTN one with nothing in sight walks back to the weapon locker and
  stands in its reach until its kit is full. HTN bots carry a kit for their weapon's role —
  a rifleman 4 spare magazines, 2 grenades and a light vest; a marksman 5, 1 and none; a launcher 3
  pouches, 3 grenades and a medium vest — and `--bot-kits default` gives them the default kit
  instead ([`docs/LOADOUT.md`](docs/LOADOUT.md) §8).
- **In the strategist's chair** one queues infantry at every barracks while the points last, keeps
  four units home defending, and attack-moves the rest at whatever its units have actually seen —
  it gets no free knowledge of where anybody is. Once it has four fighting units it buys a builder,
  and fortifies the resource nodes with a pillbox, a wall in front of it and a tower behind, facing
  the ground force's spawn. Ground bots shoot a pillbox or a tower only when no unit is in sight.
  Under the HTN commander its builder also lays sandbags across the bearing a squad reinforcing a
  zone faces, and, once it has a pillbox, the bearing its garrison faces: up to two walls, a wall
  for every three units without a spot.
  Its units fight from behind low walls, move their posts behind them, and a builder that is
  shot at hides behind cover.

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
