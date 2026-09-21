# gdpyr — Greybox Prototype Implementation Plan

Goal: answer one question as cheaply as possible — **is asymmetric FPS-vs-RTS fun enough that people
want to keep playing it?** Everything in this plan is scoped to that question. Primitive meshes, no
art, no audio pass, no progression, no matchmaking.

Target: a playable 6v2 (6 Ground Force, 2 Strategist) 20-minute round on one map, over the internet,
with friends, in roughly 4–6 solo engineering-weeks.

Companion documents:
[`NETCODE.md`](NETCODE.md) — tick model, message set, ballistics, fog of war ·
[`DEPLOYMENT.md`](DEPLOYMENT.md) — AWS EC2 dedicated server runbook.

---

## 1. Current state

### `gdpyr` (this repo)

Godot **4.6**, C# (`net8.0`, `Godot.NET.Sdk/4.6.0`), GL Compatibility renderer. It is a single-player
FPS-controller tutorial project, ~1100 lines of C#:

| Area | Files | Reusable? |
|---|---|---|
| Character controller | `Scripts/Fps/fps_controller.cs` | Yes, after refactor (§3.1) |
| Movement FSM | `Scripts/Fps/StateMachine.cs`, `State.cs`, `Player*State.cs` | Yes, after refactor |
| Weapon data | `Scripts/Fps/WeaponDefinition.cs` (`[GlobalClass] Resource`) | Yes — extended in M2 |
| Viewmodel + sway | `Scripts/Fps/WeaponInit.cs`, `WeaponCamera.cs`, `WeaponSubViewport.cs` | Yes, cosmetic only |
| Debug panel | `Scripts/Ui/Debug.cs` | Yes — host the net HUD here |
| Pause menu, reticle | `Scripts/Ui/PauseMenu.cs`, `Reticle.cs` | Yes |
| Greybox assets | `Textures/kenney_prototype/`, `Scenes/Test.tscn` | Yes |

Paths are as of M0, which moved the tutorial project into the layout in §3; M2, which renamed
`Weapons.cs` to `WeaponDefinition.cs` when it grew a combat half; and M3, which added
`Scripts/Rts/` and a fourth autoload. The two defects below were *not* fixed in M0; both were fixed
in M1, which moved the simulation onto the fixed tick and made it read recorded `InputFrame`s
rather than the device.

1. **Movement runs on the render frame.** `StateMachine._Process` → `State.Update(delta)` →
   `fps_controller.UpdateVelocity()` → `MoveAndSlide()`
   (`Scripts/Fps/StateMachine.cs:34`, `State.cs:47`, `fps_controller.cs:126`).
   `MoveAndSlide()` internally uses the *physics* delta, so movement distance per second scales with
   framerate. It must run in `_PhysicsProcess` at a fixed tick.
2. **Movement reads the global `Input` singleton directly** (`Scripts/Fps/fps_controller.cs:108`,
   `PlayerWalkingState.cs:23`). Prediction requires re-running past ticks from stored input, which is
   impossible while the simulation reads live device state.

### `pyrrhic` (Unity reference, read-only)

Unity 2022.1, Netcode for GameObjects 1.1.0, ~3600 lines. Worth porting:

| Asset | Verdict |
|---|---|
| `Assets/Scripts/NPC/Weapons/BallisticArc.cs` — `IntegrationMethods.Heuns`, `BulletPhysics`, `BulletData` | **Port nearly verbatim.** Pure math, engine-agnostic apart from `Vector3`. This is the single most valuable thing in the old repo. |
| `Assets/Scripts/Game/GameMode.cs` — `BootLives`, `StrategistTickets`, `GameDurationMinutes` | Port as a Godot `Resource`. |
| `UnitCommandType` (Move / Attack / …), `PyrrhicTeam` enums | Port the enums, drop the class hierarchy. |
| `Assets/Scripts/UI/SquareDrawer.cs`, `SelectSquare.cs` | Port the concept (screen-rect → frustum select). |

Do **not** port:

- `UnitBase` / `UnitCapability` / `CommandableCapability` — runtime `AddComponent` capability
  composition (`UnitBase.cs:27-42`). Over-engineered for a greybox; one `Unit` class driven by a
  `UnitDefinition` resource is enough.
- `FindObjectsOfType<PyrrhicPlayer>()` inside per-unit logic (`UnitBase.cs:62`, `UnitCapability.cs:18`).
  At 50 units this is a full scene scan per unit per query. Use a server-side registry.
- Rigidbody projectiles (`PyrProjectile.cs`). Replaced by analytic projectiles — see `NETCODE.md` §4.
- `PyrrhicGame.AddPlayerToTeamServerRpc` (`PyrrhicGame.cs:73-83`): it mutates a `List<T>` *inside* a
  `NetworkVariable<TeamInfo>.Value` without reassigning, so the variable is never marked dirty and
  the change never replicates. The same trap exists in Godot if you mutate a synchronized
  `Godot.Collections.Array` in place — replicate immutable snapshots instead.

---

## 2. Networking decision

### Requirement vs. reality

"Prediction and rollback" means two different things, and the distinction decides the architecture:

- **Rollback netcode** (GGPO / netfox / fighting games): every peer simulates *all* entities
  deterministically and re-simulates on input mismatch. With 50+ navmesh-driven units and a physics
  engine in the loop, full-state re-simulation at 60 Hz is both expensive and fragile (float
  determinism, Jolt/Godot Physics solver state). **Wrong tool for this game.**
- **Server-authoritative prediction + reconciliation + lag compensation** (Quake 3 / Source /
  Overwatch): the server is the only authority; each client predicts *only its own character* and
  replays unacknowledged inputs on correction; everything else is interpolated from snapshots.
  **This is what shooters with dedicated servers actually use, and it is what this design needs.**

The RTS side needs neither: 100–250 ms of command latency is normal for RTS and imperceptible.
Strategist commands are plain RPCs with immediate local UI acknowledgement.

### Options evaluated

| Option | Verdict |
|---|---|
| **Godot built-in high-level multiplayer (`ENetMultiplayerPeer` + `MultiplayerSpawner`/`MultiplayerSynchronizer`) + hand-written prediction layer** | **Chosen.** Zero dependencies, pure C#, headless dedicated server is a one-line export, transport is swappable behind `MultiplayerPeer`, and `MultiplayerSynchronizer`'s per-peer visibility is a ready-made fog-of-war mechanism. Cost: ~400–600 lines of prediction/reconciliation you own. |
| **[Netick for Godot](https://kakr.itch.io/netick-for-godot)** | Strong on paper — free, C#, engine-agnostic core, ships client-side prediction, lag compensation, snapshot interpolation, delta compression. But the Godot port is at 0.7.x, is closed-source middleware distributed via itch.io, its documentation is Unity-oriented, and adopting it means restructuring the whole project around its object model. **Worth a 1-day timeboxed spike before committing to the hand-written layer** — if the spike is smooth it saves 1–2 weeks. If anything is rough, walk away; you cannot patch it. |
| **[netfox](https://foxssake.github.io/netfox/latest/) / [NetfoxSharp](https://godotengine.org/asset-library/asset/3945)** | Core is GDScript; C# support is an explicitly experimental wrapper. Full-rollback model is the wrong fit (above), and marshalling 50+ entities per tick across the GDScript↔C# boundary is a performance risk. **Its `noray` NAT-punch relay is independently useful** and can be adopted without the rest. |
| **Steam as the foundation (GodotSteam / SteamMultiplayerPeer)** | Steam is a *transport and lobby* concern, not an architecture. As of now there are no official .NET builds of GodotSteam, C# bindings are community forks, and [expressobits/steam-multiplayer-peer](https://github.com/expressobits/steam-multiplayer-peer) paused development in Dec 2025 and points users back at GodotSteam. Pure-C# routes exist ([steam-multiplayer-peer-csharp](https://github.com/craethke/steam-multiplayer-peer-csharp), [SteamNetGodot](https://github.com/OverfortGames/SteamNetGodot)) but each carries caveats (no channel support; custom non-`MultiplayerPeer` API). **Adds dependency risk now for zero gameplay benefit.** |

### The actual "rapid prototyping with friends" answer

Connectivity, not netcode, is what blocks playtests. **The playtest strategy is a headless dedicated
server on an AWS EC2 instance with an Elastic IP**; clients connect straight to `<ip>:7777` over UDP.
Full runbook in [`DEPLOYMENT.md`](DEPLOYMENT.md).

1. Godot dedicated-server export (Linux x86_64, Strip Visuals, embedded PCK) → `scripts/deploy.sh`
   uploads and restarts a systemd unit on the box.
2. Security group: one inbound rule, Custom UDP 7777. ENet is UDP-only.
3. Clients launch with `gdpyr --client <elastic-ip>:7777`.

The server has a public address and clients dial out, so no player needs port forwarding, NAT
punch-through, or a relay. An overlay network (Tailscale/ZeroTier) is only the fallback if you ever
host on a home desktop instead.

Steam integration (lobbies + Steam Datagram Relay so friends click "Join") becomes worthwhile only
when "my friends can't connect" is the real bottleneck. Because the transport sits behind the
`MultiplayerPeer` interface, swapping ENet for a Steam peer is a change to **one factory method**
(`Scripts/Net/TransportFactory.cs`) and touches no gameplay code. Deferred to M7.

---

## 3. Architecture

```
Scripts/
  Core/        Bootstrap (CLI args, mode select), GameConfig resources, ServiceRegistry
  Net/         TransportFactory, NetworkManager, SimClock, MessageIds, snapshot/input codecs
  Sim/         Pure C#, no Godot nodes, unit-testable: Ballistics, ProjectileSim, ResourceLedger
  Fps/         PlayerCharacter (predicted), InputFrame, movement FSM, weapons, emplacements
  Rts/         StrategistController, Selection, Unit, UnitDefinition, Barracks, ResourceNode
  Match/       MatchState (tickets/points), TeamService, VisibilityService (fog), win conditions
  Bots/        BotDirector (backfill), BotPilot (ground), BotStrategist (RTS) — decisions in Sim/
  Ui/          Team select, loadout select, RTS HUD, net debug HUD
Scenes/        Greybox map, player, units, props
Tests/         xUnit project over Scripts/Sim (no engine required)
```

Rules that keep this from rotting:

- **The simulation never reads `Input` or `_Process`.** Gameplay runs in `_PhysicsProcess` at a
  fixed 60 Hz tick. Rendering/viewmodel/sway stay on `_Process`.
- **Movement is a pure function**: `Move(CharacterState, InputFrame, float dt) -> CharacterState`.
  This is the prerequisite for prediction; everything else follows from it.
- **`Scripts/Sim` has no Godot node dependencies** (Godot *math* types are fine) so it is testable
  in CI without an engine.
- **No per-tick allocations in the unit/projectile paths.** Pre-size arrays, use structs, keep a
  server-side entity registry instead of scene scans. C# GC hitches are the most likely reason a
  50-entity Godot prototype feels bad.
- Pin `physics/3d/physics_engine` explicitly in `project.godot` so server and clients agree
  (Godot 4.6 defaults new projects to Jolt; this project predates that setting).

---

## 4. Milestones

Each milestone ends in something you can play or measure. Estimates are solo-dev engineering days.

### M0 — Foundations (1 day)

- Rename `Fps 2` → `Gdpyr` (csproj, sln, `project.godot` assembly name), root namespace `Gdpyr`.
- Directory layout above; move existing scripts into it.
- `Bootstrap.cs` autoload parsing `OS.GetCmdlineUserArgs()`: `--server [port]`,
  `--client <host:port>`, `--listen` (host+play, for solo testing).
- `Linux Server` dedicated-server export preset; `Engine.PhysicsTicksPerSecond = 60`,
  `Engine.MaxFps = 60` on the server (headless Godot otherwise pegs a core).
- `Tests/Gdpyr.Tests.csproj` (xUnit, references `GodotSharp`), `dotnet test` green.
- **Stand up the AWS box and the deploy path now** — `scripts/export-server.sh`,
  `scripts/deploy.sh`, `deploy/gdpyr-server.service`, per [`DEPLOYMENT.md`](DEPLOYMENT.md). Every
  later milestone is verified against the real server, not localhost; latency bugs do not reproduce
  on loopback.
- **Done when:** `./scripts/deploy.sh` puts a build on EC2 and a client on your desktop connects to
  `<elastic-ip>:7777`, both logging matching tick counters.

### M1 — Netcode spine (4–5 days) ← *the hard part; do not rush it*

- `TransportFactory` (ENet today), `NetworkManager` autoload with Server/Client/Listen modes.
- `SimClock`: authoritative server tick; client estimates server tick from RTT and runs a jitter
  buffer (see `NETCODE.md` §2).
- `InputFrame` struct + client sampler; redundant input packets (last 3 frames, unreliable).
- **Refactor `fps_controller` + `State`/`*State` to consume `InputFrame`** and run in
  `_PhysicsProcess`. Delete direct `Input.*` reads from all simulation paths.
- Prediction + reconciliation for the local character; snapshot interpolation for remote characters.
- **Net debug HUD** inside the existing `Debug` panel: RTT, tick drift, input buffer depth,
  prediction error magnitude, mispredictions/sec, bytes in/out. Build this *now* — without it,
  every later bug is unfalsifiable.
- **Done when:** 3 capsules move smoothly on a dedicated server with 80 ms RTT and 20 ms jitter
  injected (`tc netem` on Linux / Clumsy on Windows), with no rubber-banding and prediction error
  under ~2 cm in steady state.

### M2 — Ground Force combat (5–6 days)

- Extend `Weapons.cs` → `WeaponDefinition : Resource`: fire mode, RPM, magazine, reload time,
  damage, and a `ProjectileDefinition` (mass g, radius mm, G1 BC, muzzle velocity).
- Port `Ballistics.cs` from pyrrhic (`IntegrationMethods.Heuns`, `BulletPhysics`); unit-test against
  the closed-form no-drag solution.
- `ProjectileSim`: analytic, server-authoritative, segment raycast per step, owner-predicted tracer
  (`NETCODE.md` §4).
- Loadout: hammer (melee shapecast), semi-auto pistol, and one of DMR / assault rifle / single-shot
  grenade launcher. Selection on spawn and on each respawn.
- Health, death, respawn, ground-team ticket counter.
- Weapon models: reuse the existing geopick + pistol; everything else is a scaled `BoxMesh`.
- **Done when:** two players on a dedicated server can kill each other at 100 m, leading a moving
  target, and tickets decrement to zero and end the round.

### M3 — Strategist core (6–7 days)

- Strategist camera: pan / edge-scroll / zoom / rotate, ground-plane raycast cursor.
- Box selection (port `SquareDrawer` concept) + command issue: Move, Attack, Patrol, Defend.
- `Unit`: server-only simulation. `NavigationAgent3D` over a baked `NavigationRegion3D`, FSM of
  Idle → Moving → Engaging → Dead. One class, parameterised by `UnitDefinition`
  (HP, speed, sensor radius, weapon, cost).
- Units fire the *same* `ProjectileSim` projectiles as players, with an accuracy cone.
- Barracks: build queue, spawn point, cost deduction.
- Replication: ~~`MultiplayerSpawner` + `MultiplayerSynchronizer`~~ **a packed `UnitSnapshot`
  broadcast at 20 Hz** plus reliable spawn/despawn, client-side interpolation. The deviation and
  its three reasons are recorded in [`NETCODE.md`](NETCODE.md) §6.1; the short version is that the
  built-in nodes would have been a second replication mechanism in a codebase that already hand-packs
  every message, and that filtering a packet per peer is what M4's fog of war actually needs. Budget
  check at 50 units: ~13 KB/s down per client (see §6).
- **Done when:** a strategist queues 20 infantry, orders an attack, and the ground team fights them
  in a real firefight on a dedicated server.

### M3.5 — Computer players (2 days)

The blocker on every milestone from here is that playing the game needs eight people in a voice
call. This makes one person enough to see a round.

- `BotFillPolicy` (engine-free): how many bots each side should have, given who is already there.
  Bots fill the seats nobody is sitting in and get up when somebody wants one; an empty server gets
  none at all, because a headless Godot fighting itself spends the EC2 box's CPU credits before a
  playtest starts (`DEPLOYMENT.md` §5).
- **A bot is a player.** Same peer id (from a reserved band at the top of the positive range, so
  `OwnerId`'s negative half stays the units'), same roster slot, same character, same snapshot
  record. The only difference is that `PlayerManager` takes its `InputFrame` from `BotDirector`
  instead of from a jitter buffer — one branch in the server's roster loop, and nothing downstream
  of it knows. No new message, no new authority (`NETCODE.md` §9).
- `BotBrain` (engine-free): one tick of intent as a pure function — turn towards the target at a
  capped rate, hold an aim error for a window rather than re-rolling it per tick, strafe at
  preferred range, sprint on the way, jump when wedged, fire only once it has both reacted and
  turned onto the target.
- `BotStrategist`: queues at every barracks while the points last, keeps a garrison, attack-moves
  the rest. Its intel is the union of what its own units have acquired and nothing else, so it is
  already behind the fog M4 builds.
- Ground bots do not target players, and check that nobody on their own side is in the line of fire
  before shooting: friendly fire between players is on, and a bot that kills the person it spawned
  to help is worse than no bot.
- `--bots <n>[:<m>]` / `--no-bots`, defaulting to the game mode's `BotGroundForce` and
  `BotStrategists` (6 and 1).
- **Done when:** one person can launch `--listen`, be shot at by infantry a computer strategist
  ordered, and have five bots on their side doing the same thing back — and when a second person
  joining takes a bot's seat rather than a ninth slot.

### M4 — Fog of war (2–3 days)

- Server `VisibilityService`, recomputed at 5–10 Hz: a ground-team entity is visible to a strategist
  peer iff some friendly unit is within its `SensorRadius` (optionally plus a LOS ray). Shipped at
  7.5 Hz with the ray, bounded to the three nearest sensors that cover the point — fifty units may
  cover one player and each answer costs a ray against the physics world.
- ~~Drive `MultiplayerSynchronizer.SetVisibilityFor(peerId, …)` from it~~ — that mechanism was
  replaced in M3 ([`NETCODE.md`](NETCODE.md) §6.1). The filter is applied while building each
  strategist's **player** snapshot (`PlayerManager.BroadcastSnapshot`), and to the projectile
  spawn and hit messages by where the shot happened, because a tracer leaving a muzzle says where
  somebody is just as loudly as a position record does. The *unit* snapshot stays one broadcast:
  every unit on the field belongs to the side the fog is being kept from, so filtering it per peer
  would hide nothing, and hiding units from the ground force at a 7.5 Hz sphere test would make
  them blink in and out of an FPS at forty metres. §6.2 records the deviation.
- Render "last known position" ghosts client-side that fade after N seconds. Inferred rather than
  announced: there is no "contact lost" message, only a record that has stopped arriving, so the
  client turns a 300 ms gap into a lost contact and decays the marker over eight seconds. A
  right-click picks from those contacts, so an order against a ghost goes to where they were last
  seen and one whose ghost has faded cannot be clicked at all.
- **Done when:** a strategist genuinely cannot track a flanking player, and scouting has value.

### M5 — Economy, tiers, win/lose (3–4 days)

- `ResourceNode`s with capture radius and income tick; contested state.
- Three unit tiers, **one unit each**: Infantry (T0) → Technical, HMG-armed (T1) → Tank (T2).
- Heavy-gun emplacements: pick up (movement penalty), deploy, mount, fire; ammo cans carried from
  the ground spawn to resupply.
- Win/lose: ground team loses at 0 tickets; strategist loses when points < cheapest unit cost **and**
  no units remain alive **and** no resource node is held. Round end → scoreboard → restart.

### M6 — Playtest instrumentation (1–2 days, then ongoing)

- Per-round CSV dump: round length, ticket curve over time, strategist point curve, kills/deaths and
  positions, unit losses by type, time-to-first-contact.
- Post-round 3-question in-game survey.
- **This is the actual deliverable of the whole project.** "Is it fun" is answered by round-length
  distributions and whether people ask for another round, not by intuition.

### M7 — Steam (deferred, 2–3 days when needed)

Swap `TransportFactory` to a Steam `MultiplayerPeer`, add lobby create/join. Re-evaluate the options
in §2 at that time — that corner of the ecosystem moves.

**Total: ~24–30 engineering days to end of M6.**

---

## 5. Scope cuts (deliberate — flag if you disagree)

| Cut | Rationale |
|---|---|
| **Builder units** in the first pass | Resource nodes captured by proximity test the same economy loop with far less code. Add builders in M5+ if capture-by-presence feels flat. |
| **Dropships and troop carriers** | Transport/logistics is a second system (load/unload, pathing, drop physics). Defer until the base loop is proven fun. |
| Server browser / matchmaking | Direct IP over Tailscale. |
| Animation, audio, VFX beyond placeholders | Greybox. |
| Anti-cheat beyond server authority | Server authority is free and sufficient here. |
| Multiple maps | One map, iterated on. Map layout is a top-3 fun variable — iterate the one map instead of building three. |

---

## 6. Risks

| Risk | Signal | Mitigation |
|---|---|---|
| **The core mechanic is asymmetrically boring** — a worse FPS attached to a worse RTS | Strategists stop volunteering for the strategist slot | Front-load M3/M4. Fog of war (M4) is what makes the strategist role a *game* rather than a spawn button — do not defer it. |
| `MoveAndSlide()` replay drift during reconciliation | Constant small corrections in the net HUD even when idle | All replayed ticks use the same fixed delta; restore position *and* velocity before replay; if drift persists, smooth visual error instead of snapping (`NETCODE.md` §3). |
| C# GC hitches at 50+ units | Frame spikes correlated with unit count | No per-tick allocation in unit/projectile paths; pooled projectiles; entity registry instead of scene scans. |
| ~~`MultiplayerSynchronizer` visibility gates *sync* but perhaps not *spawn*~~ | — | **Closed in M3.** Units replicate through a packed per-peer message (`NETCODE.md` §6.1), so a unit a peer is not told about has no node at all. M4 needed no spike: it applied the same filter to the player snapshot, which is now one packet per strategist rather than one broadcast. |
| Netick spike (M1) burns a day and is discarded | — | Timebox to 1 day, hard stop. |
| Realistic muzzle velocities make leading imperceptible | Players report shooting feels hitscan | See `NETCODE.md` §4.4: at 940 m/s a 6 m/s target at 100 m needs only 0.64 m of lead. Muzzle velocity is a **gameplay tuning knob**, not a realism constant — expect to run 200–400 m/s. |

---

## 7. Immediate next actions

M0 through M4 are done. M3 shipped the strategist camera and box selection, the four orders, the
`Unit` FSM over `NavigationAgent3D`, unit weapons through the same `ProjectileSim` players use,
the barracks build queue and the strategist's point pool, team selection with a two-strategist cap,
and the packed unit replicator the deviation in §M3 describes. It also closed the two items M2 left
open for whichever milestone needed them: **accuracy cones** (`NETCODE.md` §4.5 — seeded, so the
predicted tracer still matches) are in, authored at 0° for every player weapon and 2.5° for
infantry.

M3.5 made one person enough to run a round: bots backfill both sides while anybody is connected and
stand up again as people arrive. It also fixed a defect it could not live with — a dedicated server
used to spawn an unmanned character for itself, which started the round before anyone had joined
and stood at a spawn point costing the ground force a ticket every time a unit shot it.

M4 put the fog in front of the strategist. A ground-force player is in a strategist's packet only
while one of the strategist's units has them in sensor range with a clear line, and the same test
decides which projectile messages they are told about, so a flanker who opens fire no longer lights
themselves up from across the map. What is left behind is a decaying ghost rather than nothing,
because stale information is what makes the role a game. The computer strategist reads the same
service, so the two are behind one fog rather than two (`NETCODE.md` §6.2, §9).

Still open from M2: **a full hitbox rewind for projectiles** (§4.3's "upgrade" — the ring is built
and melee uses it, projectiles still use the cheap spawn-time advance). Nothing since has needed it,
and units deliberately keep no history at all (`NETCODE.md` §5).

1. **Play a round against the bots, then play one on the EC2 box.** Every number in
   `Units/infantry.tres` is a guess: 50 points, 4 s to build, 45 m of sensor, 40 m of engagement,
   2.5° of cone. So is `StrategistTickets = 1000`, which is exactly twenty riflemen. The first
   question a playtest answers is whether twenty riflemen are a threat to six players or a queue of
   free kills — and a bot round now answers a rough version of it in five minutes without booking
   eight people. Watch `GroundForceTickets = 50` especially: five bots dying like bots may end a
   round in a couple of minutes, and the fix is the ticket pool rather than the bots.
2. **Play the strategist seat now that it is dark, and watch `SensorRadiusMeters` first.** 45 m was
   picked in M3 as a *combat* number — how far a rifleman notices someone to shoot at — and M4 has
   quietly promoted it to the whole of the strategist's map knowledge. If twenty riflemen light the
   map up anyway, the answer is a smaller sensor than engagement range, which is also what makes a
   cheap scout unit worth building in M5. The debug HUD's `fog` row is the instrument: "seen"
   against "tracked", and "withheld" climbing at all.
3. **Verify the fog on the real server, not on a listen host.** A host is its own authority and
   cannot be filtered against itself, so `--listen` draws a curtain over the characters instead
   (`NETCODE.md` §6.2). It is the right shape for judging whether the fog is *fun*; it is worth
   nothing for judging whether it is *tight*. Two machines and a dedicated server is the only test
   that answers the second question.
4. **Tune `BotTraits.Default` against a person.** Turn rate 4.5 rad/s, a 3° held aim error, a 200 ms
   reaction. Those three numbers are the whole difficulty dial, and they were picked to be
   *mediocre* on purpose — a bot that beats a person in a straight fight makes the ground force look
   weak, which is the opposite of what the instrument is for.
5. **Verify the navigation bake on the real map.** It runs on the server's first tick against the
   nodes in the `navmesh` group and falls back to steering straight at the destination if it
   produces nothing — which is playable and wrong, so the debug HUD says "no navmesh" when it
   happens. Bots path through the same mesh, so a failed bake now shows up as six bots grinding
   along a wall. Check that line before drawing conclusions about pathing.
6. **M5 — economy, tiers, win/lose.** The fog makes sensor radius a unit-design lever rather than a
   combat constant, which is most of the argument for the three tiers: a scout that sees further
   than it shoots is now a real unit and not a stat block.
7. Three smaller things earlier milestones left where they were: a unit hit is tested against its
   *current* capsule rather than a rewound one (justified in `NETCODE.md` §5, but worth revisiting
   if players report missing units they clearly hit); the strategist HUD can only build from
   barracks 0 — the RPCs take an index and the HUD only ever sends zero, and the computer
   strategist already uses every barracks on the map, so the gap is only in the human's UI; and the
   ground force is not fogged at all, which is a deliberate cut with a reason (§6.2) rather than an
   oversight, but it does mean a modified ground-force client still has a map-wide unit radar.

---

## Sources

- [Godot 4.6 release notes](https://godotengine.org/releases/4.6/)
- [Netick for Godot](https://kakr.itch.io/netick-for-godot)
- [netfox](https://foxssake.github.io/netfox/latest/) · [NetfoxSharp](https://godotengine.org/asset-library/asset/3945)
- [expressobits/steam-multiplayer-peer](https://github.com/expressobits/steam-multiplayer-peer) (development paused, Dec 2025)
- [craethke/steam-multiplayer-peer-csharp](https://github.com/craethke/steam-multiplayer-peer-csharp)
- [OverfortGames/SteamNetGodot](https://github.com/OverfortGames/SteamNetGodot)
- [GodotSteam](https://github.com/GodotSteam/GodotSteam)
