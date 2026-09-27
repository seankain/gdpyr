# HTN bots — research and implementation plan

Status: **proposal**. Nothing in the game plans with an HTN yet. The only code this document adds is
the measurement in §3.2 (`tools/Gdpyr.HtnBench`, `scripts/htn-bench.sh`).

Scope: replace the goal selection of the three computer-controlled deciders — the ground bot
(`BotPilot`), the RTS unit (`UnitManager.SimulateUnit` over `UnitBrain`) and the computer strategist
(`BotStrategist`) — with hierarchical task network planners, built around five tasks: **Defend
Zone, Recon, Attack, Retreat, Resupply**. RTS units plan with their standing order, the side's
known enemy positions and the friendly units around them as inputs.

What does not change: the micro. `BotBrain.Frame` still turns intent into an `InputFrame`,
`UnitBrain` still decides Idle/Moving/Engaging and where a given order walks, `Unit.Steer` and
`ServerFire` still walk and shoot. An HTN decides *what to do*; the existing code keeps deciding
*how to do it this tick*.

---

## 1. What the bots do today

Read from the code on this branch (`04e2012`).

| Decider | Goal selection today | What it does not consider |
|---|---|---|
| Ground bot — `Scripts/Bots/BotPilot.cs` | Nearest hostile unit or armed structure its own `GroundSensor` scan can see (`AcquireTarget`, l. 243); otherwise the nearest enemy barracks, at a per-bot loiter point or at the edge of its defended ring (`Objective` / `TryFindObjective`, l. 370–445). | Resource nodes — it never walks to one, so bots never deny income, which M5 made the ground force's reason to move. Anything a teammate has seen. Where teammates are, except to avoid shooting them. Its own health. The weapon locker — a bot's large weapon is fixed by roster slot (`BotDirector`, l. 433), so four in six can never hurt a tank. |
| RTS unit — `UnitManager.SimulateUnit` (l. 353), `Scripts/Sim/UnitBrain.cs` | A pure FSM: nearest ground-force player inside its own sensor with a clear ray (`UnitManager.AcquireTarget`, l. 460), or the order's named target; destination from the order (`UnitBrain.Destination`, l. 130). | Contacts the side already has through `VisibilityService`. Friendly units nearby: no focus fire, no waiting for the group, no answering a friend in a fight. Its own health. A defending garrison stands on one point. |
| Strategist — `Scripts/Bots/BotStrategist.cs` | Every 30 ticks: queue the best affordable tier; the first four units by registry order `Defend` the first barracks' rally point; every other unit attack-moves to the nearest *currently visible* player, or to the ground spawns in turn every 25 s (`TryObjective`, l. 513); one builder fortifies nodes. | Squads: the whole assault is one blob given one point. Defending the nodes that are its income (only the barracks is garrisoned). Scouting — the technical's 70 m sensor is never used on purpose. Retreat: units fight until dead. Last-known positions: ghosts are ignored by design (`TryObjective` doc). A human strategist sharing the army: units are owned by the *team* (`ApplyOrder`, l. 798–817), so the bot re-orders a human's units whenever their order differs from its plan. |

Two properties the current code has and the plan keeps:

- **Decisions are engine-free and tested.** `BotBrain`, `UnitBrain` and `StrategistBrain` live in
  `Scripts/Sim` and are pure; `Tests/*BrainTests.cs` cover them without Godot.
- **Bots have no privileges.** The strategist sees through the same `VisibilityService` a human's
  packet is filtered by and acts through the same `ServerQueueUnit` / `ServerIssueOrder` /
  `ServerConstruct` a human's RPC lands in; a ground bot sees with its own eyes.

---

## 2. Why an HTN

An HTN planner decomposes a root task through an ordered list of methods, each guarded by
conditions on a world state, into a queue of primitive tasks; primitive tasks carry effects so the
planner can look ahead within the decomposition (Humphreys, *Exploring HTN Planners through
Example*). What that buys over the alternatives, for this codebase:

| Approach | Fit here |
|---|---|
| FSM / hand-written selection (today) | Every new goal is a new branch in imperative code, and priorities between goals are implicit in statement order. It is what produced §1's gaps. |
| Behaviour tree | Reactive only: no effects, so no "go to the locker, *then* the tank is attackable" lookahead. Re-evaluates from the root every tick unless hand-cached. |
| GOAP | Plans by search over actions; cost grows with action count and designer control over *which* plan comes out is indirect (action costs). |
| Utility | Good at scoring "which node / which target", poor at sequencing. Used *inside* HTN conditions and operators here, not instead of them. |
| **HTN** | Author-ordered priorities are explicit (method order), plans are short and readable, a higher-priority method can pre-empt a running plan (§3.3), and the same planner serves every layer — which is how Killzone 3's bots were built: commander, squad and individual layers on an HTN planner, orders down, information up, individuals still autonomous in combat (Straatman et al., *Hierarchical AI for Multiplayer Bots in Killzone 3*). |

The RL policies of M6–M7.5 are not replaced by this. They take a seat through the agent API and the
seat falls back to the bot when the policy goes quiet (`BotDirector.ServerTick`, l. 92;
`BotDirector.Sample`, l. 112); that branch is untouched. What changes is the *opponent* those
policies are trained and evaluated against — see D5 in §8.

---

## 3. .NET HTN libraries

### 3.1 Survey

| Library | Licence | Runtime | Last activity | Engine dependency | Verdict |
|---|---|---|---|---|---|
| **[FluidHTN](https://github.com/ptrefall/fluid-hierarchical-task-network)** (Pål Trefall) | MIT | C#, ~3,400 lines, no dependencies beyond the BCL | Commit `e67af26`, 2026-03-18 | None (checked: no `UnityEngine` reference in the library folder) | **Use.** Total-order forward decomposition after Humphreys; Select/Sequence compound tasks, conditions, executing conditions, three effect types, partial planning (`PausePlan`), domain splicing and run-time slots, a method traversal record (MTR) for plan priority, an `IFactory` seam for pooling, 159 upstream unit tests. |
| FluidHTN on NuGet (`Fluid-HTN`) | MIT | .NET Standard 2.0 | Only version is `0.0.0`, published 2021-09-01 | None | **Do not use.** Predates four and a half years of upstream fixes; vendor the source instead (§3.3). |
| [CHP — C# HTN-Planner](https://sourceforge.net/projects/chpplanner/) (P. van Gastel) | GPL-3.0 | C#, plus a Unity variant | 2013 (CHP 1.0.1 / CUHP 1.1.0); SourceForge is file hosting only | None / Unity | **Reject.** GPL-3.0 cannot be combined into this MIT repository without relicensing it; dormant since 2013. |
| [Tencent/behaviac](https://github.com/Tencent/behaviac) | BSD-3-Clause | C++ core, C# runtime | 2023 | Designer is Windows-only; behaviours are authored in it and exported | **Reject.** A BT/FSM/HTN framework whose workflow is its editor; a second toolchain for a greybox with no designers. |
| [SandboxAI](https://github.com/OneManMonkeySquad/SandboxAI) | MIT with a no-resale clause | C# | Archived 2025-05-04 | `UnityEngine` | **Reject.** Unity-bound and archived. |
| [UnityHTN](https://github.com/konbraphat51/UnityHTN) | — | C# | 2024-07 | Unity | **Reject.** Unity-bound, 3 stars. |
| [godot-fluid-hierarchical-task-network](https://github.com/fnaith/godot-fluid-hierarchical-task-network) | MIT | C++ GDExtension port of FluidHTN | — | Godot | **Reject.** Every condition and operator would cross the C#/C++ boundary, and `Scripts/Sim` may not depend on engine nodes (IMPLEMENTATION_PLAN.md §3). |
| Write our own | — | C# | — | None | **Fallback.** A struct-based planner fits the house style, but re-deriving MTR pre-emption, partial plans and their tests is days of work FluidHTN has done. Worth it only if §3.2's numbers were bad; they are not. |

Reference-only, not libraries to embed: SHOP / JSHOP2 (Lisp / Java, academic), Pyhop / GTPyhop
(Python), PANDA and HDDL (academic hierarchical planning systems and their domain language). None
targets per-tick replanning inside a game server.

### 3.2 Measurement: FluidHTN on net8.0

Question: does FluidHTN build on the game's target framework, and does it break the rule every unit
path keeps — no per-tick allocation (IMPLEMENTATION_PLAN.md §3)?

Method (`tools/Gdpyr.HtnBench/Program.cs`): FluidHTN's library sources at `e67af26`, compiled into a
net8.0 console app with `Nullable` off, as this repo's projects are. One domain shaped like §5.1's
(five goals, three levels, ~20 primitives), shared by 64 agents — `SimConfig.MaxUnits`, the largest
population anything here plans for. Each agent's planner ticks every server tick; its facts change
on a 10-tick stagger (the unit target refresh), and in a second run on every tick for every agent.
Operators only count, so the planner is measured and not the game. 3,600 ticks (one minute at
60 Hz) after a 2,000-tick warm-up, `TieredCompilation` off so both rows run optimised code.
Allocation is `GC.GetAllocatedBytesForCurrentThread`.

```
./scripts/htn-bench.sh                     # pooled IFactory
./scripts/htn-bench.sh --default-factory   # FluidHTN's DefaultFactory
```

Result, 4-core Intel Xeon @ 2.10 GHz, .NET SDK 8.0.131:

| Factory | Facts change | Replans / minute | Planner time per server tick, 64 agents | Allocated | gen0 GCs |
|---|---|---|---|---|---|
| `DefaultFactory` | every 10 ticks | 23,040 | 27.4 µs (0.16% of a tick) | 2,371,320 B (658.7 B per tick) | 0 |
| `DefaultFactory` | every tick | 230,391 | 65.4 µs (0.39%) | 6,452,544 B (1,792.4 B per tick) | 0 |
| pooled (`PooledFactory` in the bench) | every 10 ticks | 23,040 | 21.4 µs (0.13%) | **0 B** | 0 |
| pooled | every tick | 230,391 | 59.7 µs (0.36%) | **0 B** | 0 |

Timings moved by up to ~35% between runs on this shared VM (the pooled steady row was 21.4 µs in
one run and 29.1 µs in the next); the byte counts are exact and repeated identically on every run.

Findings:

1. It builds on net8.0 with **0 warnings, 0 errors**, unchanged, once `Properties/AssemblyInfo.cs`
   is left out. Left in, `Gdpyr.csproj` fails with `CS0579: Duplicate 'AssemblyVersionAttribute'`
   — observed when a checkout sat inside the project directory, which the Godot SDK's default glob
   compiles whether or not git ignores it.
2. **FluidHTN allocates on every replan by default** — about 100 B per replan at the realistic
   cadence (`Sequence` borrows an `int[]` of world-state depths per decomposition,
   `BaseContext.GetWorldStateChangeDepth`). Over a 20-minute round that is ~47 MB of garbage.
3. **It allocates nothing with a pooled `IFactory`.** Every borrowed array and queue is returned
   through `Free*`, so a per-type free list is enough. The pooled factory is part of H0.
4. Time is not the constraint: even replanning every agent every tick is under 0.4% of the tick
   budget. The real cost will be the conditions and sensors the game supplies, which is why facts
   are refreshed on the existing scan cadence (§4.2) rather than every tick.

### 3.3 Recommendation and the constraints that come with it

**Vendor FluidHTN at `e67af26` into `ThirdParty/FluidHTN/`**: the `Fluid-HTN/` library folder only,
without `Properties/AssemblyInfo.cs` or its old-style `.csproj`, with upstream's `LICENSE` beside it
and the commit hash in a `README` line. Not upstream's `Fluid-HTN.UnitTests/`: it is MSTest, and the
same glob as §3.2's finding 1 would compile it into the game. `Gdpyr.csproj` picks the library up
through the SDK's default glob; `Tests/Gdpyr.Tests.csproj` needs one
`<Compile Include="../ThirdParty/FluidHTN/**/*.cs" />`. It is MIT and small enough to own outright
if upstream stops.

What reading its source says the integration must respect:

| FluidHTN property | Consequence here |
|---|---|
| World state is a `byte[]` indexed by an enum; `SetState` marks the context dirty only when a value *changes*. | Facts are **discretised** (health bands, force-ratio bands, contact kinds). Positions, target ids and distances live on the context as ordinary fields that conditions read. Only a change worth replanning for goes in the world state — that is the anti-thrash lever. |
| A dirty replan replaces the running plan only if the new decomposition is strictly higher priority by MTR (`Selector.BeatsLastMTR`). | Retreat can pre-empt Attack mid-plan, which is the point. The converse needs care: a running Attack is *not* replaced by a lower-priority Defend when the contact disappears. **Every long-running operator gets an executing condition** (`ExecutingCondition`) that fails when its premise goes, or the bot finishes an obsolete plan. |
| `Selector` and `Sequence` hold a per-instance `Plan` queue. | One domain instance is shared by every agent of a kind, which is safe only because the server tick is single-threaded. Planning must never move to a worker thread without per-thread domains. |
| Slots (`Slot` / `TrySetSlotDomain`) belong to the domain, not the agent. | Per-unit-type behaviour (builder, tank, technical) is a separate domain built once, not a slot swapped per agent at run time. |
| No random selector in the core. | Planning is a pure function of facts and MTR, as `Scripts/Sim` requires. Any variety (which loiter point, which flank) comes from `Spread.Seed`, as `BotBrain` does today. |

---

## 4. Architecture

### 4.1 Layers

```
 STRATEGIST SIDE                                   GROUND SIDE
 ───────────────                                   ───────────
 Commander HTN  (BotStrategist, every 30 ticks)    Ground coordinator (every 30 ticks)
   │ missions: Defend Zone / Recon / Attack /        │ roles: node deniers, locker runner,
   │           Retreat / Resupply per squad          │        focus targets, buddy pairs
   │ + production and fortification                  │
   ▼  ServerIssueOrder / ServerQueueUnit / …         ▼  GroundRole on each pilot
 SquadBoard  (server-only: squad → units,          Team contact memory (union of the
   mission, phase, focus target, staging point)      ground seats' own GroundSensor scans)
   │                                                 │
   ▼                                                 ▼
 Unit HTN  (per unit, on its 10-tick scan)         Ground bot HTN  (per bot, on its 10-tick scan)
   │ standing order is the top-level task            │
   ▼  UnitIntent                                     ▼  GroundIntent
 UnitBrain + Unit.Steer + ServerFire  (unchanged)  BotBrain.Frame → InputFrame  (unchanged)
```

Orders go down as the orders that already exist; information goes up only through the shared
boards. Nothing new goes on the wire (§4.4).

### 4.2 Rules

1. **Engine-free planning.** Domains, contexts, fact encoders, boards and intents live in
   `Scripts/Sim/Htn/` and are tested by `dotnet test`. Operators never call the engine: a primitive
   task writes an **intent** (a struct: goal point, target preference, stance, buttons) onto its
   context, and the engine half — `BotPilot`, `UnitManager`, `BotStrategist` — carries it out.
2. **No new privileges.** The strategist and its units know what `VisibilityService` knows
   (live contacts and ghosts, `TryContact`). A ground bot knows what its team's seats have seen
   (D3). Every action goes through the requests a human's RPC lands in.
3. **Zero allocation on the tick.** Pooled `IFactory`; fixed-size boards pre-sized from `SimConfig`;
   an xUnit guard that plans over every domain and asserts `GetAllocatedBytesForCurrentThread`
   does not move (H0).
4. **Cadence, not every tick.** Facts are encoded on the scans that already exist and are already
   staggered: a unit's `NextScanTick` (10 ticks), a ground bot's `_nextScanTick` (10 ticks), the
   strategist's decision interval (30 ticks). The planner's `Tick` still runs every tick so
   operators progress, but it replans only when an encoded fact changed or the plan ended.
5. **Deterministic.** No RNG; ties broken by id or `Spread.Seed`.
6. **Switchable.** `--bot-ai legacy|htn` (`LaunchOptions`) and a `BotAi` field on
   `GameModeDefinition`. `legacy` is today's code path, kept intact, and stays the default until H6.

### 4.3 Shared knowledge

| Board | Side | Contents | Fed from | Cost |
|---|---|---|---|---|
| `ContactMemory` | one per side | Fixed array of contacts: owner id, kind (infantry / armour / armed structure / player), last-known position and velocity, last-seen tick, seen-by count. Ages out on `SimConfig.GhostLifetimeTicks`, as the strategist's ghosts do. | Strategist: `VisibilityService` after each refresh. Ground: each ground bot's `GroundSensor.Scan`. | Updated on existing scans; no new rays. |
| `Neighbourhood` | per unit / per bot | Friendlies within 30 m: count, summed health, how many are engaging, nearest friend, centroid; hostiles from `ContactMemory` within 40 m. Gives the **local force ratio** band. | Positions already in `UnitManager` / `CombatManager`. | 64 units: ≤ 4,096 distance checks per full refresh, spread over the 10-tick stagger. A uniform grid only if the debug HUD says so. |
| `ZoneBoard` | one per side | Per zone — each resource node, each barracks, each ground spawn — holder, contested, friendly strength, estimated enemy strength (cost-weighted, armour counted against explosives available), ticks since last observed. | `EconomyService` node state, `ContactMemory`, unit census. | 8 nodes + barracks + spawns; rebuilt per commander decision. |
| `SquadBoard` | strategist | Up to 8 squads: members, mission, target zone, staging point, phase (gathering / moving / engaged / falling back), focus target, strength at formation. | Written by the commander; read by unit HTNs. Also written by `ApplyOrder` for a human's order (below). | Fixed arrays, `SimConfig.MaxUnits` members total. |

**Human orders form squads too.** `ApplyOrder` already receives the units of one order as a batch.
Recording that batch as an ad-hoc squad (and the issuing peer on each unit, server-side) gives a
human's selection the same cohesion and focus fire as a bot's — and lets the commander HTN leave a
human's units alone (D1).

### 4.4 Where it plugs in

| File | Change |
|---|---|
| `ThirdParty/FluidHTN/` | Vendored library (§3.3). |
| `Scripts/Sim/Htn/` (new) | `PooledHtnFactory`, fact enums, `GroundContext` / `UnitContext` / `CommanderContext`, the three domain builders, `ContactMemory`, `Neighbourhood`, `ZoneBoard`, `SquadBoard`, `GroundIntent` / `UnitIntent`. |
| `Scripts/Bots/BotPilot.cs` | `Sample` asks the ground domain for a `GroundIntent` instead of calling `Objective`; `AcquireTarget` prefers the intent's target. `OutsideDefences`, `Reachable`, stuck recovery, the friendly-fire and blast checks stay. `BotBrain` gains a `Use` press (a locker tap). |
| `Scripts/Bots/BotDirector.cs` | Owns the ground coordinator and the team `ContactMemory`; builds pilots and commanders with the selected `BotAi`. Agent fallback branches unchanged. |
| `Scripts/Rts/UnitManager.cs` | In `SimulateUnit`, after acquisition and before `UnitBrain.Destination`, run the unit's planner and apply its `UnitIntent` (destination override, target preference, hold). `ApplyOrder` records the order batch as a squad and the issuer on the unit. |
| `Scripts/Rts/Unit.cs` | Server-only fields: squad id, order issuer, HTN context. Nothing replicated. |
| `Scripts/Bots/BotStrategist.cs` | `ServerTick` becomes the commander domain's tick; `Build` / `Fortify` / `StrategistBrain` survive as its primitive tasks. |
| `Scripts/Core/LaunchOptions.cs`, `Scripts/Match/GameModeDefinition.cs` | `--bot-ai`, `BotAi`. |
| `Scripts/Ui/Debug.cs`, `Scripts/Core/ConsoleCommands.cs` | A plan row on the debug HUD; `bot_plan <n>` prints a bot's current task chain from FluidHTN's `OnNewTask` / MTR debug. |

No message, codec, snapshot field or agent observation changes. A demo records frames and "no bot
thinks" during playback (DEMOS.md), so demos are unaffected.

---

## 5. Domains

Selectors are in priority order: first valid method wins, and a higher one can pre-empt a running
plan (§3.3). `[…]` is a condition. Operators marked *existing* wrap code that exists today.

### 5.1 Ground bot

Facts (one byte each, encoded on the pilot's scan):

| Fact | Values |
|---|---|
| `Contact` | none · ghost (team saw one ≤ 8 s ago) · visible to me |
| `ThreatKind` | infantry · armour (bullet-proof unit or armed structure) |
| `Armed` | small arms only · explosive in hand |
| `Health` | ok (≥ 60%) · hurt · critical (< 30%) |
| `Odds` | favourable · even · outnumbered (local hostiles ≥ 2 × friends within 30 m) |
| `AlliesNear` | a teammate within 15 m |
| `Role` | assault · denier · locker runner (from the coordinator) |
| `AtZone` | inside the assigned node's capture radius |
| `InsideDefences` | inside an enemy barracks' ring (today's `OutsideDefences` test) |
| `MagazineLow` | today's `needsReload` |

```
ground
├─ Retreat
│   ├─ leave defences         [InsideDefences]                          existing OutsideDefences as an operator
│   └─ fall back to allies    [Health=critical ∧ Odds=outnumbered]      move to the nearest teammate cluster, else the spawn;
│                                                                       executing condition: still outnumbered
├─ Resupply                   [Contact≠visible]
│   ├─ swap at locker         [Role=locker runner ∧ ThreatKind=armour ∧ Armed=small arms]
│   │                           path to locker → tap Use until an explosive is in hand (≤ 2 taps: the locker cycles
│   │                           rifle → launcher → DMR → rifle)
│   └─ reload                 [MagazineLow]                             existing BotBrain reload
├─ Attack                     [Contact≠none]
│   ├─ engage armour          [ThreatKind=armour ∧ Armed=explosive]     existing blast-safe hold-fire applies
│   ├─ press with allies      [AlliesNear ∧ Odds≠outnumbered]           coordinator's focus target; advance to preferred range
│   │                                                                   keeping the buddy within 15 m
│   ├─ engage                 [Contact=visible]                         existing BotBrain range-hold and strafe
│   └─ investigate ghost      [Contact=ghost]                           move to last-known position, sweep, give up at 8 s
├─ Defend Zone                [Role=denier]
│   ├─ hold node              [AtZone]                                  stand in the capture radius facing the nearest barracks;
│   │                                                                   one body stops the node paying (M5)
│   └─ take node              move into the capture radius              effect AtZone
└─ Recon
    ├─ sweep stalest zone     coordinator's least-recently-observed node
    └─ standoff               existing Objective(): loiter at the enemy barracks' ring
```

**Ground coordinator** (team level, every 30 ticks, a plain function rather than a planner — it
assigns, it does not sequence): one denier per strategist-held node nearest the ground spawn, up to
a third of the team; at most one locker runner while armour is in `ContactMemory` and fewer than two
bots hold an explosive; a focus target per contact cluster (lowest health first, then nearest);
buddy pairs by proximity. Humans are counted for strength but never given roles.

### 5.2 RTS unit

The standing order is the top-level task: the planner chooses *how* to carry it out and never
*whether*. Facts, encoded on the unit's 10-tick scan:

| Fact | Values |
|---|---|
| `Order` | mirror of `Unit.Order.Kind` |
| `Contact` | none · side-known (a `ContactMemory` entry within sensor range of the unit or its anchor) · own target |
| `Health` | ok · hurt · critical |
| `Odds` | favourable · even · outnumbered, from `Neighbourhood` |
| `FriendEngaged` | a squad-mate or a friend within 40 m is engaging |
| `SquadPhase` | gathering · moving · engaged · falling back, from `SquadBoard` |
| `InLeash` | self and target inside the defend leash (today's `UnitBrain.Destination` test) |
| `Stance` | obey (last order from a human) · autonomous (last order from a bot) |

```
unit
├─ Retreat                    [Stance=autonomous ∧ Health=critical ∧ Odds=outnumbered ∧ Order∉{Move,Build}]
│                               walk to the nearest friendly cluster or the squad's staging point without stopping to fight
├─ Resupply                   [Stance=autonomous ∧ Health≠ok ∧ Contact=none ∧ D2 adopted]   return inside the barracks ring until healed
├─ Build                      [Order=Build]                                existing; builder steps to its nearest friend when shot at
├─ Attack                     [Order=Attack]
│   ├─ wait for squad         [SquadPhase=gathering]                       hold at the staging point
│   ├─ engage focus           [Contact=own]                                squad focus target if in range, else own; existing hold-while-engaging
│   ├─ support                [FriendEngaged]                              move towards the engaged friend's target
│   └─ advance                existing attack-move towards the order's point or last-known target position
├─ Defend Zone                [Order∈{Defend,None}]
│   ├─ engage in leash        [Contact=own ∧ InLeash]                      existing leash
│   ├─ answer a call          [FriendEngaged ∧ InLeash]                    converge on the engaged friend
│   └─ hold post              take post k of n on a ring round the anchor, facing the threat bearing from ZoneBoard
├─ Recon                      [Order=Patrol]                               existing patrol; target = squad focus; does not stop for a contact it can report
└─ Obey                       [Order=Move]                                 existing: go there, shoot on the way; target = squad focus
```

A `Move` order is never second-guessed: `UnitOrder` documents it as "go there … does not chase",
and a strategist flanking with it has already decided the risk.

### 5.3 Strategist commander

Every 30 ticks, over `ZoneBoard`, `ContactMemory` (live contacts *and* ghosts — D4), the economy and
`SquadBoard`. Facts are per decision; which zone or squad a method binds to is chosen by scoring in
its condition (utility inside HTN, §2).

```
commander
├─ Retreat                    [a squad's strength < 40% of formation ∧ estimated enemy ≥ 1.5 × squad]
│                               Move order to the nearest held zone with friends; phase=falling back; merge on arrival
├─ Defend Zone                [a held node or barracks is contested, or ZoneBoard threat within 60 m]
│   ├─ reinforce              nearest idle or reserve squad with strength ≥ 1.2 × threat: Defend order at the zone
│   └─ buy time               no squad fits: rally fresh production to the zone; fortify it (existing Fortify)
├─ Attack                     [a target zone or contact cluster where squad strength ≥ 1.5 × estimate]
│   ├─ stage                  Move to a staging point outside the target's sensor; phase=gathering until ≥ 80% present
│   ├─ strike                 Attack order on the target, named contact as TargetOwnerId; phase=engaged
│   └─ escort armour          tanks go only with an infantry squad; they share its staging and strike
├─ Recon                      [no live contact for 20 s ∨ a zone unobserved for 60 s]
│                               one technical (70 m sensor, 8 m/s) — else one rifleman — on a Patrol through the
│                               stalest zones; Move, not Attack: a scout's job is to see
├─ Resupply                   [a squad is below 60% strength and not engaged]
│                               pull it to the rally point; route the next produced units into it; heal if D2 adopted
└─ Economy                    existing StrategistBrain.TryChooseTier / ShouldQueue / builder / Fortify as primitives,
                                with production directed at the squad the plan above is short of
```

Garrison stays as today's first `GarrisonUnits` defending the first barracks, now a standing
`Defend Zone` squad. Units a human ordered in the last 30 s are not re-ordered (D1).

### 5.4 The five tasks at each layer

| Task | Ground bot | RTS unit | Strategist commander |
|---|---|---|---|
| **Defend Zone** | Hold a strategist node to stop its income (denier role) | Spread to posts round the anchor, answer friends' fights inside the leash | Send a squad sized to the threat to a contested node or barracks |
| **Recon** | Sweep the least-recently-seen node; investigate ghosts | Patrol without stopping for contacts it can report | One technical through stale zones on a patrol |
| **Attack** | Focus the coordinator's target with a buddy; armour only with an explosive | Wait for the squad, then focus fire; support engaged friends | Stage out of sight, strike when assembled, escort armour |
| **Retreat** | Leave barracks defences; fall back to teammates when critical and outnumbered | Back to friends when critical and outnumbered (autonomous stance only) | Pull a losing squad back to a held zone and merge |
| **Resupply** | Locker run for a launcher when armour is known; reload between fights | Heal inside the barracks ring (D2) | Refill a depleted squad from production; heal (D2) |

---

## 6. Phases

Estimates in solo-dev days, as IMPLEMENTATION_PLAN.md §4 counts them. Each phase ships behind
`--bot-ai htn` and leaves `legacy` untouched.

### H0 — Planner in the tree (0.5 day)

- Vendor FluidHTN at `e67af26` (§3.3); `Tests` include line; `PooledHtnFactory` in `Scripts/Sim/Htn`.
- `--bot-ai legacy|htn` and `GameModeDefinition.BotAi`, parsed and tested in `LaunchOptionsTests`.
- `HtnAllocationTests`: plan and tick a representative domain 1,000 times after warm-up; assert zero
  bytes allocated.
- **Done when:** `dotnet test` passes with FluidHTN compiled in, and `./scripts/htn-bench.sh` uses the
  vendored copy and still reports 0 B.

### H1 — Shared knowledge (1.5 days)

- `ContactMemory`, `Neighbourhood`, `ZoneBoard`, `SquadBoard`, engine-free, fixed-size, tested
  (ageing, capacity overflow, force-ratio bands, squad membership when a unit dies or is re-ordered).
- Feeds: `VisibilityService` → strategist memory after each fog refresh; `GroundSensor` scans →
  ground memory; `ApplyOrder` → ad-hoc squads and issuer.
- **Done when:** the debug HUD shows each side's contact count and squad count, and the numbers
  match what is on the field in a `--listen` round.

### H2 — Ground bot HTN (2–3 days)

- Ground facts, domain (§5.1), coordinator; `BotPilot` consumes `GroundIntent`; `BotBrain` taps `Use`.
- `GroundHtnTests`: for each row of the domain, a fact vector and the task chain it must produce —
  e.g. armour known + small arms + locker runner → `[path to locker, use locker]`; critical +
  outnumbered while in `Attack` → the plan is replaced by `Retreat`.
- **Done when:** in a `--listen` round with `--bot-ai htn`, bots stand on strategist nodes (the HUD's
  `nodes` row shows contested nodes), a bot fetches a launcher after a tank is seen, and the
  `locker_launcher_vs_tank` scenario still passes.

### H3 — Unit HTN (2 days)

- Unit facts, domain (§5.2), `UnitIntent` applied in `SimulateUnit`; defend posts; squad focus.
- `UnitHtnTests` over fact vectors; existing `UnitBrainTests` unchanged and passing.
- **Done when:** a human's selection given one attack order arrives together and focuses one target;
  a garrison spreads round its anchor; per-tick server time on the debug HUD is within noise of
  `legacy` at 64 units.

### H4 — Strategist commander (3 days)

- Commander domain (§5.3) over the boards; `Build` / `Fortify` as primitives; human-claim rule.
- `CommanderHtnTests`: contested node + idle squad → `Defend Zone` on that node; no contact for 20 s +
  a technical → `Recon`; squad at 30% vs 2× enemy → `Retreat`; existing `StrategistBrainTests`
  unchanged.
- **Done when:** in a bot-only round the strategist retakes a denied node, a technical scouts, and a
  losing squad withdraws rather than dying in place.

### H5 — Unit resupply (0.5–1 day, only if D2 is adopted)

- `RegenPerSecond` on `UnitDefinition`, applied inside a friendly barracks' defended ring; tested in
  `DefenseTests` style.

### H6 — Scenarios, playtest, switch the default (1 day, then ongoing)

- New scenarios in `Tests/Scenarios/`, distributional as AGENT_API.md §3 requires, e.g.
  `htn_ground_denies_node.json` (strategist nodes contested for ≥ N ticks), `htn_strategist_defends_node.json`,
  `htn_recon_finds_contact.json`, `htn_squad_retreats.json` (units lost per engagement vs `legacy`).
  Metrics beyond today's vocabulary need events on `AgentEventBus` (e.g. a squad mission change),
  which is an agent-API change and is documented there when made.
- Playtest with people; compare rounds against `legacy` on the M8 columns (round length, outcome,
  units lost by tier, nodes held over time).
- **Done when:** the default flips to `htn`, and `docs/TRAINING.md` / `RL_ARCHITECTURE.md` say which
  bot a trained policy was evaluated against.

Total: about 10–12 days before playtesting.

---

## 7. Verification

| Claim | Proof |
|---|---|
| The planner allocates nothing on the tick | `HtnAllocationTests` (H0) in `dotnet test`; `./scripts/htn-bench.sh` |
| A given situation produces a given plan | Fact-vector → task-chain tests per domain (H2–H4); no engine needed |
| Legacy behaviour is intact | Existing `BotBrainTests`, `UnitBrainTests`, `StrategistBrainTests` unchanged; `--bot-ai legacy` default until H6 |
| The bots behave as described in a real round | Scenarios in H6, run by `./scripts/playtest.sh` |
| It costs nothing noticeable | Debug HUD server-frame-time row, `legacy` vs `htn`, 64 units |

---

## 8. Decisions needed

| # | Question | Recommendation |
|---|---|---|
| D1 | Should unit-level HTN act on units a **human** strategist ordered? | Yes for how an order is carried out (cohesion, focus fire, defend posts, answering calls); no for autonomous Retreat/Resupply, which only units last ordered by a bot get (`Stance`). The bot commander does not re-order a unit a human ordered in the last 30 s. |
| D2 | RTS units have nothing to resupply: magazines reload without a reserve and nothing heals. Add healing inside a friendly barracks' defended ring? | Yes — one `RegenPerSecond` per `UnitDefinition`. Without it, unit Resupply means reinforcement only, and unit Retreat saves a unit that can never recover. |
| D3 | May a ground bot know what a **teammate** has seen? | Yes, as a team `ContactMemory` — the equivalent of voice callouts, and less than a human client already draws (the unit snapshot is not fogged for the ground force, IMPLEMENTATION_PLAN.md §M4). Today a bot knows only its own eyes (`BotTraits.SensorRadiusMeters`). |
| D4 | May the computer strategist act on **ghosts** (last-known positions)? `BotStrategist.TryObjective` deliberately does not. | Yes, for Recon and for staging only, and never to name a target: a human strategist can click a ghost (M4), so the bot doing so is parity, not cheating. |
| D5 | The scripted bots are the RL baseline ("beats the bot" should stay true, RL_ARCHITECTURE.md). | Keep `legacy` selectable for ever; `scripts/evaluate.sh` and the trainer pin `--bot-ai legacy` until a deliberate re-baseline, and training output records which one was used. |

---

## 9. Risks

| Risk | Signal | Mitigation |
|---|---|---|
| Plan thrash: facts flicker across a band edge and plans churn | Debug HUD plan row changes every scan | Hysteresis on every band (the 1.25 × sensor drop rule `UnitBrain.ShouldDropTarget` uses); only banded facts in the world state |
| Obsolete plans run to completion | Units attack a contact that left | Executing conditions on every long-running operator (§3.3); a test per operator that its premise failing fails the plan |
| Bots become too good | Ground force loses every round with bots on | Every threshold (force ratios, strength fractions, radii) on `BotTraits` / `StrategistTraits`; coordinator caps (one locker runner, a third as deniers) |
| Order semantics drift for human strategists | Players report units "not doing what they were told" | §5.2's rule: the order is the top-level task; `Move` is never second-guessed; autonomous Retreat only on bot-ordered units |
| Coordination costs rays | Fog or scan time grows on the HUD | Boards reuse existing scans and `VisibilityService`'s rays; no new line-of-sight queries in H1–H4 |
| Vendored library diverges from upstream | Upstream fixes a bug we hit | Pinned commit in `ThirdParty/FluidHTN/README`; `./scripts/htn-bench.sh` and the allocation test re-run on any bump |

---

## Sources

- FluidHTN — <https://github.com/ptrefall/fluid-hierarchical-task-network> (read at `e67af26`,
  2026-03-18: `Planners/Planner.cs`, `Contexts/BaseContext.cs`, `Factory/DefaultFactory.cs`,
  `Tasks/CompoundTasks/Selector.cs`, `README.md`)
- `Fluid-HTN` on NuGet — <https://www.nuget.org/packages/Fluid-HTN> (registration API: one version,
  `0.0.0`, 2021-09-01)
- CHP: C# HTN-Planner — <https://sourceforge.net/projects/chpplanner/>
- Tencent behaviac — <https://github.com/Tencent/behaviac>
- SandboxAI — <https://github.com/OneManMonkeySquad/SandboxAI>
- UnityHTN — <https://github.com/konbraphat51/UnityHTN>
- godot-fluid-hierarchical-task-network — <https://github.com/fnaith/godot-fluid-hierarchical-task-network>
- T. Humphreys, *Exploring HTN Planners through Example*, Game AI Pro, ch. 12 —
  <https://www.gameaipro.com/GameAIPro/GameAIPro_Chapter12_Exploring_HTN_Planners_through_Example.pdf>
- R. Straatman, T. Verweij, A. Champandard, R. Morcus, H. Kleve, *Hierarchical AI for Multiplayer
  Bots in Killzone 3*, Game AI Pro, ch. 29 —
  <http://www.gameaipro.com/GameAIPro/GameAIPro_Chapter29_Hierarchical_AI_for_Multiplayer_Bots_in_Killzone_3.pdf>
- T. Verweij, *A hierarchically-layered multiplayer bot system for a first-person shooter* (2007) —
  <https://www.guerrilla-games.com/media/News/Files/VUA07_Verweij_Hierarchically-Layered-MP-Bot_System.pdf>
- JSHOP2 — <https://github.com/mas-group/jshop2>
