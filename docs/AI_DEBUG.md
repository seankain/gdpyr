# AI debugger and spectating

Status: **built.** A person can join any round as a **spectator** — no character, a free camera
— and turn on an **AI debugger** that shows, for any bot, unit or commander squad they click, the
HTN of [`HTN_BOTS.md`](HTN_BOTS.md) with the task running, the tasks planned behind it and the
tasks set aside behind a pause marked on it; the facts it planned on and which conditions hold;
what its planner has done lately; and, in the world, where its plan is taking it. The computer
strategist's commander gets the same, squad by squad. The same text is on the console
(`ai_plan`, `ai_tree`, `ai_squads`), which is what [`HTN_BOTS.md`](HTN_BOTS.md) §4.4 asked of
`bot_plan <n>`.

Scope: debugging the three HTN deciders — the ground bot (§5.1), the RTS unit (§5.2) and the
commander squad (§5.3). Nothing a bot decides changes: the debugger reads the planners outside
their tick and writes nothing back (§6). Legacy bots (`--bot-ai legacy`) have no plan, and the
debugger says so.

---

## 1. Quick start

| To | Do |
|---|---|
| Watch bots play each other, on your own | Main menu → **Watch the bots**, or `godot --path . -- --spectate` (straight past the menu into an offline round), or `-- --listen --spectate` to let others join |
| Spectate a server | `-- --client <host[:port]> --spectate`, or **Spectate (F3)** on the role menu, or `F3` / `spectate` at any time |
| Stop spectating | `F3`, `F1`/`F2`, or `play`: you come back as somebody who has just joined, and pick a side |
| Inspect something | Left-click a ground bot or a unit; shift-click to add up to four |
| See the strategist bot's plan | `F6`: its squads in the world, and a *strategist* tab listing them; click a row or a squad's ring for that squad's plan |
| See every agent at once | `F5`: each planning bot's and unit's task over its head, with a line to where it is going |
| Read it as text | `~` then `ai_plan bot 3`, `ai_plan unit 12`, `ai_plan squad 2`, `ai_squads`, `ai_tree ground` |

A dedicated-server **export** does not send its bots' plans unless started with `--ai-debug`; every
other build does unless started with `--no-ai-debug` (§6).

---

## 2. Spectating

### 2.1 What a spectator is

A **connected peer with no character.** It is in no roster, no snapshot record and no team. The
wire's team is one bit ([`Team`](../Scripts/Sim/Team.cs)) and stays one bit; nothing that counts
players, spawns them, asks them for a side, gives them tickets or fogs their packets learns about
a third kind of peer.

| Question | Answer, and where |
|---|---|
| How does a peer become one? | It asks (`ClientSpectate`, reliable). The authority despawns its character for everybody, files it in its spectator set, and tells it (`ServerSpectating`, sent *before* the despawn so the free camera is up first). `PlayerManager.Spectators.cs` |
| And stops? | It asks again. It gets what joining gets: a fresh character at a free spawn, and the role menu. A spectator's seat is left the way a join is entered, under the same strategist cap |
| What is it sent? | Everything every peer is sent. `ReplicateSnapshot` filters only for strategists, and a spectator is not one, so it gets the ground force's unfiltered roster; units and structures are not fogged for anybody but a strategist already |
| What does it send? | Nothing per tick. `ClientTick` stops sampling and sending input while spectating, and the server would drop it anyway (no `Player` to queue it for) |
| Can it order units, pick a loadout, use a locker? | No: every one of those requests is checked against the sender's side or character, and a spectator has neither |
| Rate limit | One change a second per peer (`SpectateCooldownTicks`): spectating spawns and despawns a character |
| A listen host or an offline round | Its own player can spectate; it is then its own spectator, and its frames go to its own panel without a socket |
| A demo | Starting a demo ends spectating: a demo is already watched from outside it (DEMOS.md) |

### 2.2 The seat

`Scripts/Ui/Spectator/`: `SpectatorView` (selection, switches, the watch request),
`SpectatorCamera`, `AiDebugPanel`, `HtnGraphView`, `AiDebugWorld`.

| Input | Does |
|---|---|
| left click | inspect what is drawn nearest the pointer (36 px): a ground player, a unit, or with `F6` on a squad's middle; on empty ground, clear the selection |
| shift + left click | add it (up to four), or let go of it if it is already selected |
| right drag | look (the pointer is a cursor otherwise) |
| WASD, space, crouch, sprint | fly; they also let go of anything being followed |
| `F` | orbit what the panel's current tab shows; right drag walks round it |
| `Tab`, `shift+Tab` | the next or previous ground player |
| Backspace, Delete | clear the selection |
| `F3` | stop spectating |
| `F4` | hide or show the panel |
| `F5` | every agent: a label over each planning bot and unit, and a line to where it is going |
| `F6` | the strategist: each computer strategist's commander, in the world and in a tab |
| `F7` | the domain as a graph rather than a text tree |
| `F8` | world markers on or off |
| `F9` | fold everything off the plan's path (on to start with) |

The switches are also along the top of the panel, for the mouse.

### 2.3 Bots run for spectators

`BotFillPolicy` (`Scripts/Sim/BotRoster.cs`) gives an empty server no bots, so that a box nobody
is on burns no CPU. A spectator is somebody: `BotDemand.Spectators` counts them, a server with
only spectators fills both sides, and a spectator takes no seat — three playing and three
watching get the bots three playing get (`BotRosterTests`).

---

## 3. Reading a planner

All engine-free, in `Scripts/Sim/AiDebug/`, and tested by `dotnet test` (§8).

### 3.1 Domain maps

`HtnDomainMap.Build` walks a FluidHTN domain depth-first and numbers every task and every
condition, planning and executing, in walk order, keeping each node's parent, depth, branch
(its index among its parent's subtasks — the number a method traversal record holds), children
and effects.

| | Tasks | Conditions |
|---|---|---|
| ground (§5.1) | 34 | 52 |
| unit (§5.2) | 34 | 60 |
| squad (§5.3) | 24 | 16 |

*(Measured on this branch; `ai_tree` prints them. The wire allows 255 of each.)*

**A task travels as its index.** The server numbers the domains its bots plan with
(`GroundPlanning.Map`, `UnitPlanning.Map`, `CommanderPlanning.Map`); a spectator builds the same
domains from the same code (`HtnDomains`) and numbers them the same way. The frame carries the
server's map **signatures** — FNV-1a over every node's kind, parent, name, conditions and effects
— and a spectator whose own signature differs says *signature mismatch* rather than drawing the
wrong tree. Building the ground domain without its executing conditions — the same tasks, fewer
conditions — changes the signature (`HtnDebugTests`).

### 3.2 Traces

`HtnTracer.Capture(map, context, history, trace)` reads a live context into an `HtnTrace`:

| Field | From FluidHTN | Meaning |
|---|---|---|
| `Current` | `PlannerState.CurrentTask` | the task **running** |
| `Planned` | `PlannerState.Plan` | the tasks **planned** behind it, in order |
| `Paused` | `PartialPlanQueue` | the tasks after a `PausePlan`: part of the plan, decomposed only once what is before it has run (§3.4, P5) — the commander's strike while a squad stages |
| `Traversal` | `MethodTraversalRecord` | the branch taken at each selector; a new plan replaces this one only if it beats it (P2, P3) |
| `Facts` | `WorldState` | one byte per fact |
| `Holds` | each condition's `IsValid` | whether each condition of the domain holds **now** |
| `History` | `HtnHistory` | §3.3 |

Conditions are evaluated in execution mode, against the world state the agent is executing
against, and read only: no condition in these domains writes. A condition that throws reads as
not holding; the debugger must never be what takes a server down. A planned task whose condition
does not hold yet is usually planned through a prediction — `EngageArmour` after `UseLocker`
predicts `Armed:=Explosive` (P4) — and the tree shows both the failing condition and the effect.

### 3.3 History

`HtnHistory` is a ring of the last 16 planner events per agent — new plan, plan replaced (and
what it interrupted), task started, succeeded, failed, ended because an executing condition
stopped holding (and which), stopped because its plan was replaced, not started because its own
condition failed, and plan dropped by the engine (a death, a new order, a squad rebound). It is
fed by FluidHTN's `IPlannerState` callbacks, which fire on transitions only, and is attached to
every context the three `*Planning.CreateContext` methods make, from the first tick.

It costs a reference lookup and a ring write per transition, and **allocates nothing**: a test
runs 16 ground bots through pre-emptions, failed executing conditions and successes for 3,000
ticks with it attached and measures 0 bytes (`PlanningWithHistory_AllocatesNothingOnTheTick`), and
the existing zero-allocation guards of `UnitHtnTests` and `CommanderHtnTests` run with it on.
HTN_BOTS.md §4.2 rule 3 holds.

### 3.4 Names

`HtnSchema` turns a fact byte back into what the domain's conditions call it — `Contact=Visible`,
`Order=Patrol`, `Task=Attack` — and a goal byte into its name. `AiGoals.Family` puts every goal of
every domain in one of §5.4's five tasks (plus a unit's Build and Obey), which is what the overlay
colours by: a retreat is red on every layer.

---

## 4. The views

### 4.1 Text

One renderer, `HtnText`, for the console (plain) and the panel (Godot BBCode):

```
ground
├─ retreat  (+2)
├─ resupply  (+2)
├─ attack  (+4)  [-Contact!=None]
├─ defend zone  (+2)  [-Role=Denier]
└─ recon
   ├─ sweep stalest zone  (+1)  [-Sweep=True]
   └─ standoff
      └─ >> Standoff  running 0.3s
```

`>>` is the running task, `#n` the n-th task planned, `||` a task behind a pause; `[+…]` a
planning condition that holds, `[-…]` one that does not, `{…}` the executing conditions, `=>` a
prediction. Folded (`F9`, the default), a compound task off the plan's path is one line with its
child count. Beside the tree, the plan on one line (`running Stage 4.1s  ‖ after the pause Strike
MTR 2.0`), the facts as a table, and the history newest first:

```
-  0.3s  LeaveDefences ended: while InsideDefences=True stopped holding
-  0.4s  replanned to LeaveDefences, interrupting Standoff
```

`AiDebugText.Describe` puts a header on top — who it is, its role or order, health, goal and its
family, how it moves, what it is shooting and its focus — and `SquadText.Table` lays out a
commander: one row per squad with role, size, task, target, goal, phase, strength against what it
formed with, and flags.

### 4.2 Graph

`F7` draws the domain as boxes, depth across and leaves down (`HtnGraphLayout`, a tidy tree on
its side: five levels and twenty-odd leaves fit a panel that way and do not fit it upright). The
running task is filled green, planned tasks amber with their place, paused ones blue; the path
the plan came down is outlined and its links bright. A dot on a box is green when all its
conditions hold and red when one does not; hovering a box lists them with `+`/`-`.

### 4.3 World

`AiDebugWorld` draws through walls, unshaded, once a render frame:

| Over | Draws |
|---|---|
| a selected agent | a ring in its selection colour; its name and goal; a line to every place its plan knows — where it is going, its denied node, buddy, fall-back point, locker, ghost, the armour it would engage, its stale node; for a unit its order's point and anchor, the named target's last-known position, its squad's middle, its post, staging point, the fight it would support, supply — labelled; a red line to what it is shooting and an amber one to its focus |
| every planning agent (`F5`) | its goal (and a ground bot's role) over its head, in its family's colour, and a line to where it is going |
| each commander squad (`F6`) | a ring at its middle sized by its members, lines from its members, a line to where its order sends it, its staging area while it gathers, and a red line to its fall-back point while it falls back |

---

## 5. On the wire

`Scripts/Sim/AiDebug/AiDebugCodec.cs`. Both messages start with a version byte; decoding is
total — anything but exactly one well-formed message is refused without throwing, at every
truncation, with a trailing byte, and over 20,000 random payloads (`AiDebugCodecTests`).

### 5.1 References

An `AiEntityRef` is a kind (player, unit, squad), an id (peer id, unit id, or the strategist's
peer id) and, for a squad, its slot: 6 bytes.

### 5.2 The watch request

Client → server, reliable, when it changes and every 3 s regardless: flags (labels for every
agent, the commanders), which commander (0 for all), and up to four references. 7–31 bytes. The
server reads it only from a peer in its spectator set and only up to 64 bytes.

### 5.3 The frame

Server → one spectator, unreliable, every 6 ticks (10 Hz), only while its watch asks for
something. Tick, bot AI, the three signatures; labels (19 bytes each with a point); commanders
with their squads (members, points, strengths, and each squad's trace); inspections (a trace,
markers, values). A trace is its domain, running task, status, planned / paused / traversal
indices, facts, a bit per condition and up to 16 history events.

| Measured | Bytes | At 10 Hz |
|---|---|---|
| Nothing asked for (empty frame) | ≤ 24 | — (not sent) |
| A real round: 10 labels, 1 commander, 2 inspected | 620–665 | 6–7 KB/s |
| Worst case: 80 labels, 2 commanders × 8 squads × 8 members, 4 inspected | 5,892 | 59 KB/s |

A late frame is dropped rather than shown over a newer one. The authority's own spectator gets
the same bytes decoded locally, so a listen host sees what a spectator across the room sees.

---

## 6. The authority's side

`Scripts/Bots/AiDebugPublisher.cs` builds frames from `BotDirector` (each ground bot's
`GroundContext`, each computer strategist's `Commander`), `UnitManager` (each unit's
`UnitContext`) and `CombatManager`, after the tick (`PlayerManager.PublishAiDebug`, last in
`ServerTick` and `OfflineTick`). It is read-only, and runs only while a spectator's watch asks
for something; it allocates the frame it builds, at 10 Hz, off the planners' path.

**Who gets frames.** A peer in the spectator set, never a player: a player asking is a player
asking for the other side's intentions. And only from an authority that allows it:

| Build | Default | Override |
|---|---|---|
| Dedicated-server export (`OS.HasFeature("dedicated_server")`) — the box with a public address in [`DEPLOYMENT.md`](DEPLOYMENT.md) | off | `--ai-debug` |
| Anything else: `--server` / `--listen` from a checkout, the menu's Host, offline | on | `--no-ai-debug` |

Spectating itself is allowed everywhere: a spectator sees what a ground-force client is already
sent. What `--ai-debug` guards is the plans — where the commander is about to stage, which node a
denier is walking to — which no player is sent. A process that is its own authority reads its own
bots' plans either way: they are in its memory, not on a wire, and `ai_plan` works on a listen
host whether or not it is spectating.

---

## 7. Console

| Command | Does |
|---|---|
| `spectate`, `play` | what `F3` does |
| `ai_tree <ground\|unit\|squad>` | the bare domain with every condition, its task and condition counts, and its signature |
| `ai_plan [bot <n> \| peer <id> \| unit <id> \| squad <slot> [bot <n>]]` | one agent's header, plan, facts, folded tree and history; with nothing, what the panel's tab shows. On the authority it reads the planners directly; on a client, while spectating, from the newest frame, or the next one if it has to ask |
| `ai_squads` | each computer strategist's commander table |
| `ai_select <target>` | inspect it in the panel |

`squad 2` with no strategist named is the first computer strategist's. `bot3`, `u12` and `s2 b7`
work too (`AiTargets`). The console's output is now monospaced, so trees and tables line up.

---

## 8. Verification

| Claim | Proof |
|---|---|
| The maps number each real domain as one tree; server and spectator agree; a different build disagrees | `HtnDebugTests`: parent/child/depth/branch consistency, every primitive a goal, every condition listed once; `GroundPlanning.Map` = `HtnDomains.Ground` and siblings; no-executing-conditions build changes the signature |
| A trace shows running, planned and paused correctly | `HtnDebugTests`: Standoff with nothing planned and MTR `4.1`; a locker run `PathToLocker` running with `UseLocker`, `EngageArmour` planned and `EngageArmour`'s condition failing until the prediction comes true; a staging squad with `Strike` behind the pause, then running once assembled |
| History records why tasks end, without allocating | `HtnDebugTests`: pre-emption (`ReplacePlan` naming `Engage`), executing-condition failure naming `while InsideDefences=True`, engine reset; 0 bytes over 3,000 ticks × 16 bots |
| Text and graph | `HtnDebugTests`, `AiDebugTextTests`: marked tree lines, folding, rich text escapes its own brackets and strips back to the plain text, layout puts every leaf on a row and every parent among its children |
| The wire | `AiDebugCodecTests`: round trip of every field, refusals, noise, the 5,892-byte worst case |
| Bots fill for spectators, who take no seat | `BotRosterTests` |
| `--spectate`, `--ai-debug`, `--no-ai-debug` | `LaunchOptionsTests` |
| It works in the engine | Run on Godot 4.6 .NET, this branch: a headless `--server` and a `--client … --spectate` under Xvfb (OpenGL): the server despawned the client's character, counted it, filled 1 strategist and 6 ground bots, and the client received 665-byte frames and drew the panel, tree, graph, strategist tab and world overlay. A `--listen --spectate` host spectated, came back to the role menu, and spectated again. A `--server --no-ai-debug` sent a spectator no frames and told it so |

`dotnet test`: 1382 passed, 0 failed (1312 before; 70 new). `dotnet build Gdpyr.csproj`: 0
warnings, 0 errors.

---

## 9. What it found, and what it does not do

**Found on its first run — ring-edge plan thrash.** A ground bot on `Standoff` walks to the
edge of the enemy barracks' defended ring; `InsideDefences` turns true there, Retreat's "leave
defences" pre-empts, the bot steps out, `InsideDefences` turns false, the executing condition
ends `LeaveDefences`, and `Standoff` is planned again — a transition every 0.1–1.1 s in the two
histories captured (two runs, the same bot), for as long as the bot stood there. That is HTN_BOTS.md §9's *plan thrash* signal, on a fact the
§5.1 table gives no hysteresis. It is reported here, not fixed: the fix (hold `InsideDefences` a
couple of metres past the edge once entered, or stand `Standoff` further out) changes what the bots
do and belongs with the tuning H6 lists.

**Not done:**

- No dedicated-server operator view: a headless server has no console window, so its operator
  spectates it from a client.
- The overlay draws the five task families' colours and the selection's; it does not draw
  `ContactMemory` ghosts or `ZoneBoard` threat, which the commander decides over.
- No recording: a demo does not carry the debug frames, and playback runs no bots (DEMOS.md).
- An agent seat driven by an external policy shows no plan: the policy is deciding, not the HTN.
