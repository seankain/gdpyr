# Unit tactics — implementation plan

Status: **plan; nothing built.** Read from the code at `6213d05`. Extends the unit HTN of
[`HTN_BOTS.md`](HTN_BOTS.md) §5.2 and the cover of [`COVER.md`](COVER.md).

Scope: strategist units that fight well without micro. Five requirements:

1. Heavy gunners suppress a known enemy position and anchor an assault by riflemen.
2. Snipers engage from range out of concealment: hide, pop out, fire, duck, relocate.
3. Every unit on foot takes cover from where it was last fired upon.
4. Every unit with grenades throws them at known enemies behind cover.
5. A Move order to a control point uses 1–4 instead of marching into fire.

What does not change: legacy (`--bot-ai legacy`, the RL baseline, D5); `UnitBrain`'s
Idle/Moving/Engaging state, `Unit.Steer`, `WeaponSim`; the damage model; nothing about a player's
aim or view (NETCODE.md §10.8). Planning stays engine-free, allocation-free and deterministic
(HTN_BOTS.md §4.2).

---

## 1. What the code does today

| Requirement | Today | Gap |
|---|---|---|
| 1. Gunner suppression | `Suppression.Remember` / `TryPoint` (`Scripts/Sim/Suppression.cs`): a unit with `SuppressSeconds` (heavy gunner: 4 s) that loses sight of its own target keeps firing at the last aimed point (`UnitManager.SimulateUnit`, `UnitManager.cs:470`; `Suppress`, `:847`). | A reflex only. No fire on a position the gunner did not itself see, no role in its squad, nothing waits on its fire. The commander never trains gunners or snipers: neither appears in `Commander.cs` or `BotStrategist.cs`. Ground bots do not perceive fire: no suppression fact in `GroundDomain.cs`, `BotPilot.cs` or `BotBrain.cs`, so suppression costs a bot only the rounds that hit it. |
| 2. Sniper concealment | The rifleman's domain and branches. `sniper.tres`: 70 m sensor and engage range, 80 hp; the marksman rifle fires 30 rpm. | No notion of being seen. `CoverBoard` spots are "fight over a low wall" or "hide behind anything" (builders); COVER.md §10: no peeking round a block. Units cannot crouch: `Unit.Hitbox` is the definition's full height (`Unit.cs:249`), so a unit behind a 1.2 m wall always shows its head. |
| 3. Cover from the shooter | `UnitManager.Damage` (`:1547`) sets `Unit.LastDamagedTick`; `attackerOwnerId` is carried "only so that the agent API's `unit_lost` record can name who did it". `UnitFact.UnderFire` (hit ≤ 2 s) is read by Build only. | No position for a shot. Near misses are not seen. Cover is sought from the unit's own target, the nearest remembered contact (builders) or the ring's threat (`SenseCover`, `:1230`), never from the shooter. A unit shot by someone it cannot see stands in the open. |
| 4. Grenades | `ServerThrow` (`:709`): at the current target only, while Engaging, when `suppressing \|\| AimsHigh` or the target is slow. Low arc only (`GrenadeSim.TrySolve`). | Never at a contact the unit cannot see, so never at a player crouched fully behind a wall (COVER.md §3: a crouched head does not show). No check that the arc clears the wall. No coordination: every unit in range may throw at one player. |
| 5. Move to a control point | Right-click defaults to `OrderKind.Move` (`StrategistController.cs:140`). The unit domain's "obey" follows the order, firing on the walk. `UnitFact.Objective` is set only for an Attack naming no contact (`SenseObjective`, `UnitDomain.cs:577`). HTN_BOTS.md §5.2: "A `Move` order is never second-guessed". | A person's move to a node walks the straight route through whatever is shooting at it. |

---

## 2. Rules

HTN_BOTS.md §4.2 holds. Added:

1. **The order decides where and whether; the unit and its squad decide how.** One §5.2 rule
   changes: a Move to a control point is an objective (T-D1). Every other Move gains only "react
   to fire", under a cap. A new order, `Rush`, is today's Move exactly.
2. **Every deviation is bounded.** A tactic that leaves the order's line has a distance bound, a
   time cap, and a give-up that turns it off for a while (HTN_BOTS.md §3.4, P4). A squad pinned
   for ever is the most likely failure of this plan (§9).
3. **A unit knows where a round came from, not who fired it or where they are now**, with an error
   that grows with range. That knowledge is the unit's and its squad's. It does not enter the
   side's `ContactMemory`, so the commander, the fog and the strategist's agent observation are
   unchanged (D3, D4).
4. **No status on people.** A person is suppressed by rounds and nothing else (NETCODE.md §10.8). A
   ground *bot* gets a perception fact, because a bot that ignores fire it survives makes
   suppression unmeasurable in bot-only rounds (§5.7).
5. **No new rays.** Near misses are analytic (segment against capsule). Concealment, peeks, bound
   spots and grenade arcs are `CoverBoard` box arithmetic. A preferred shooter replaces the
   acquisition search's ray when its line is clear, as the squad focus does (HTN_BOTS.md §5.2).

---

## 3. Architecture

```
 Commander HTN (bot strategist)          person / policy seat / scenario script
   │ + trains gunners and snipers,                 │
   │   one gunner per assault squad (T7)           │
   ▼  ServerIssueOrder                             ▼  ApplyOrder
 SquadBoard  (one order batch = one squad; unchanged)
   │
   ▼  NEW  squad drill, per board squad, every 10 ticks                   NEW  FireMemory, per unit
   │       threat point · roles · phase · bound spots · grenade claims        ▲ hits and near misses
   ▼  DrillBoard                                                              │ from CombatManager
 Unit HTN  (§5.2 + the facts and branches of §5)  ─────────────────────────────┘
   ▼  UnitIntent  (+ FireAt, ThrowAt, HoldFire, Crouch)
 UnitManager: Steer · ServerFire (+ at a point) · ServerThrow (+ at a remembered point) · posture
```

| Piece | Where | What |
|---|---|---|
| Projectile origin | `ProjectileSim.cs` (`Projectile`, `ProjectileSegment`) | `Origin`: where the round left the muzzle. Server-side; the spawn message already carries it. |
| `FireMemory` | `Scripts/Sim/FireMemory.cs` (new) | Per unit: last perceived fired-from point, tick, kind (near miss · hit · blast), shooter owner id, rounds in the window. Pure functions, as `Suppression`. |
| `NearMiss` | `Scripts/Sim/NearMiss.cs` (new), called from `CombatManager.ResolveSegment` (`CombatManager.cs:623`) | A round's chord, up to what it hit, against every enemy body near it. Feeds `FireMemory` (units) and the ground sensor (bots). |
| `Doctrine` | `UnitDefinition` export, mirrored in `UnitTraits` | `Rifleman`, `Gunner`, `Marksman`, `None` (builders, vehicles, trucks). Explicit in each `.tres`, not inferred from `SuppressSeconds` or range. |
| `CoverBoard` additions | `Scripts/Sim/Cover.cs` | `Concealed` (no remembered threat's eye sees a point); `TryFindPeek` (a hide spot and a fire spot beside it); `CoverQuery.Toward` and `MinProgressMeters` (spots that gain ground). |
| Posture | `Unit.cs`; `UnitSnapshot.cs` (`UnitFlags` bit 7, documented as spare); `UnitDefinition.CrouchHeightMeters` | Units on foot crouch: lower hitbox and eye, slower, no fire. Drawn from the bit. |
| Squad drill | `Scripts/Sim/Htn/DrillDomain.cs`, `SquadDrill.cs` (new) | A FluidHTN domain per board squad: fire and manoeuvre. Roles are a plain function (`DrillRoles.Assign`), as the ground coordinator's are. Writes a `DrillDirective` per squad. |
| `GrenadePlan` | `Scripts/Sim/Grenades.cs` | Choosing a remembered contact behind cover; `TrySolveOver`: the low arc, else the high one, checked against `CoverBoard` boxes. |
| Unit domain | `Scripts/Sim/Htn/UnitDomain.cs` | The facts and branches of §5. |
| Engine half | `Scripts/Rts/UnitManager.cs` | Encodes the new facts; carries out the new intent fields. |
| Ground domain | `GroundDomain.cs`, `BotPilot.cs` | `GroundFact.Suppressed` (§5.7). |
| Orders | `UnitOrder.cs`, `StrategistController.cs`, `AgentCommand.cs`, `tools/Gdpyr.Playtest/Scenario.cs` | `OrderKind.Rush = 9` (§5.6). |
| Debugger | `Scripts/Sim/AiDebug/` | The drill as a domain kind; new facts and goals in `HtnSchema` / `AiGoals`; world lines for threat points, fire lines and bound spots. |
| Agent events | `Scripts/Sim/Agent/AgentEvents.cs` | `unit_task` (a unit started one of §5's goals), `squad_drill` (a drill phase change), `grenade_thrown` (thrower's catalog id, whether the target was hidden). For scenarios; additive. |

---

## 4. The threat picture

### 4.1 Fired upon

| | |
|---|---|
| Hits | `CombatManager.DamageUnit` (`:1004`) passes the round's origin to `UnitManager.Damage`, which records it beside `LastDamagedTick`. |
| Near misses | In `ResolveSegment`, for a ground-force round, after `FirstContact`: the chord from `From` to the contact point against every strategist unit on foot whose capsule axis it passes within `NearMissMeters` (1.5 m) of, the victim excluded. Broad phase: the chord's bounding box grown by 1.5 m. A fused round counts once, on landing, for units within its blast's reach (7 m). |
| Melee | The attacker's position. |
| Perceived origin | The true origin moved sideways by `FiredFromErrorFraction` × range (0.1: 6 m at 60 m), seeded by `Spread.Seed(unit owner, round id)`. Direction well, range roughly. |
| Window | `FiredUponTicks` (3 s) after the last round. `UnitFact.FiredUpon` = none · near miss · hit, held for the window. Build's `UnderFire` becomes `FiredUpon = hit`; its meaning is unchanged. |
| Shooter | `AcquireTarget` (`:765`) tries the shooter after a named target and before the squad focus, by the focus's rule: one ray, replacing the search's when clear. Returning fire needs a line to the shooter. |

### 4.2 The squad's threat point

The drill (§5.5) picks one point per squad on its cadence. Candidates: the side's remembered
contacts (live, or ghosts ≤ `ThreatGhostTicks`, 4 s) within 60 m of the squad's centroid or of the
line to its objective, and its members' fired-from points (≤ 3 s). Winner: the most members fired
upon from within 5 m of it, then nearest the objective line, then lowest owner id. The
**suppression point** is that point raised to `SuppressAimMeters` (1.3 m) above its ground: rounds
aimed there strike the top of a 1.2 m wall or the head over it.

A ghost as a place to fire at, never as a target to name, is within D4; units already walk to
ghosts (HTN_BOTS.md §5.2, "support").

---

## 5. Behaviours

The unit domain after this plan. New branches in bold; `{…}` are executing conditions; every new
operator also holds only while its order does (HTN_BOTS.md §5.2, "Order").

```
unit
├─ Retreat                      unchanged
├─ **React to fire**            [FiredUpon≠none ∧ Contact≠own ∧ FireCover≠none ∧ ¬Bounding
│   │                            ∧ Order∉{Rush,Build,Embark,Unload}]                          §5.1
│   ├─ return fire from cover   [FireCover=in]  ReturnFire {FiredUpon≠none, FireCover=in}
│   └─ get to cover             TakeCoverFromFire {FiredUpon≠none, FireCover≠none}
├─ Resupply                     unchanged
├─ Build                        "hide behind cover" hides from the fired-from point when there is one
├─ **Marksman**                 [Doctrine=marksman ∧ (Order∈{Attack,Defend,None} ∨ Objective)]       §5.3
├─ **Objective**                [Objective: an Attack naming no contact, or a Move, at a point on a node] §5.6
│   ├─ wait for squad
│   ├─ **drill**                [DrillRole≠none ∧ DrillPhase≠move]                                  §5.5
│   └─ hold the point from cover · hold the point · take the point     moved here from Attack
├─ Attack                       today's, for an attack not on a point; **drill** above engage from cover
├─ Defend Zone                  unchanged; React to fire covers a ring shot at from beyond its sensor
└─ Recon · Obey · Carry         unchanged; Rush is Obey
```

### 5.1 React to fire — requirement 3

| | |
|---|---|
| Who | Every unit on foot with a weapon. Not vehicles, aircraft, supply trucks or riders. |
| Facts | `FiredUpon` (§4.1); `FireCover`: `CoverState` against the fired-from point, banded every tick as `Cover` is. |
| Spot | A low wall it can fire over within 10 m, else anything that hides it (a block) within 6 m. Under a Move or an objective: no further from the destination than the unit is now plus 3 m, and within 6 m of the line to it — cover on the way, not behind. A unit that must stand on a point (`ControlPoint.Holds`) searches inside the point's bound only, as `HoldPointCover` does. |
| Operators | `TakeCoverFromFire`: to the spot, firing on the move. `ReturnFire`: in the spot, fire at the fired-from point for `ReturnFireTicks` (2 s) after each round that reaches it, then hold — crouched once T3 exists. A gunner's fire is §5.2's. |
| Ends | The window (3 s after the last round); a target of its own (engage-from-cover takes over, against the target); the spot no longer hiding it; a new order. |
| Cap | Under a Move, `ReactMaxTicks` (8 s) in total since the order; then the branch is off for that order until the next hit, so a Move still arrives. |
| Not | A unit in a drill's moving element (a bound is already a move to cover, §5.5). Rush. |

This is also the tactical move: a Move that is shot at hops to cover near its route, answers, and
goes on.

### 5.2 Heavy gunner: base of fire — requirement 1

| | |
|---|---|
| Alone | No target, a threat point within its 45 m engage range (its own `FireMemory`, or the side's memory, §4.2) and a line over cover to it: `SuppressPoint` fires `SuppressBurstTicks` (1 s, ≈ 9 rounds at the LMG's 550 rpm) every `SuppressCycleTicks` (2 s) at the suppression point. Today's 4 s reflex stays. |
| Ammunition | The 150-round belt is ≈ 33 s of that cycle. Below `BeltReserveRounds` (30) it fires only in a drill's bound and assault phases. It reloads (6 s) in cover, crouched once T3 exists. |
| In a drill | Always the base element. `TakeFirePosition` is the squad's first move after contact: a low-wall spot 20–40 m from the threat point it can fire over at the suppression point, preferring a bearing ≥ 30° off the assault's axis. The drill does not bound until the base is firing, or 6 s have passed (§5.5). |
| Lift and shift | Unit rounds pass through friendly units (`UnitManager.QuerySegment` skips the shooter's team), so lifting fire is about tempo, not safety: once the assault element is within 8 m of the point the gunner shifts to the next remembered contact within 25 m beyond it, else bounds onto the objective. |
| Leading the assault | The gunner sets the tempo: its position is the first move after contact, its fire is the trigger for the bounds, and the commander builds assault squads round gunners (T7). A drill whose gunner dies falls back to a rifle half as its base. |

### 5.3 Sniper: concealment, pop out, relocate — requirement 2

| | |
|---|---|
| Threats | Remembered contacts within 90 m (live, or ghosts ≤ 8 s) and its own fired-from points. |
| Concealed | For every threat, a `CoverBoard` box crosses the line from the threat's eye (1.5 m) to the unit's head at the posture it would hold there. Range alone is not concealment: a unit cannot tell a bot with a 45 m sensor from a person with a scope. |
| Peek pair | `CoverBoard.TryFindPeek`: a concealed hide spot, and a fire spot within 2.5 m of it with a clear box line to the target's last-known eye. Behind a block, the fire spot is one slot past its edge (nothing new needed). Behind a low wall, it is the same spot, crouched then standing (needs T3). Search: 35–65 m from the primary threat, within 25 m of the sniper, inside its leash under Defend. Best: shortest walk, then fewest threats that see the fire spot, then the widest angle off its squad's axis (a flank). |
| Facts | `Doctrine`; `Peek` none · hidden · exposed; `PeekTarget` (a contact in band the fire spot sees); `Standoff` (a threat inside 30 m); `Burned` (fired upon while exposed, or two peeks from one pair). |
| Domain | `displace` [Burned ∨ Standoff] `Relocate`: to a pair ≥ 8 m from the last fire spot and ≥ 6 m from any it fired from in the last 20 s. `engage` [Peek=hidden ∧ PeekTarget ∧ hidden ≥ `MinHideTicks` (2 s)] `PopOut` → `FireFromPeek` {≤ `PeekMaxTicks` (4.5 s), ≤ `ShotsPerPeek` (2; the rifle fires every 2 s)} → `Duck`. `hide` [a pair exists] `ToHide`, holding fire beyond 30 m on the way (`UnitIntent.HoldFire`). No pair: fall through to the order's branches, as a rifleman. |
| Targets | The squad's focus when in band; else a contact a friend is engaging (it is looking the other way); else the slowest, the weakest, the lowest id. |
| In a drill | Overwatch: not an element. Its peeks are timed to the bound phases, so it fires while an element moves. |

### 5.4 Grenades at known enemies behind cover — requirement 4

| | |
|---|---|
| Who | Any unit on foot with grenades (`UnitDefinition.Grenades`, 3 by default). |
| Candidates | On the unit's scan: the side's contacts last seen ≤ 3 s ago and its squad's fired-from points ≤ 3 s old, 6–28 m from its eye (`GrenadeSim.MinThrowMeters` / `MaxThrowMeters`), that a `CoverBoard` box hides from it (the line from its eye to the contact's chest crosses one). Nearest the objective line first, then nearest. Today's current-target path stays as it is. |
| Arc | `GrenadeSim.TrySolveOver`: today's low arc, its integrated path (`Carry`) checked chord by chord against `CoverBoard.Occludes`; if a box stops it, `LowArc`'s high root when it lands with ≥ `FuseMarginSeconds` (0.3 s) of fuse left. No arc, no throw. |
| Claims | One claim per contact — per 3 m cell for a fired-from point — for `GrenadeClaimTicks` (4 s), on the squad's `DrillBoard`, or the side's for a unit in no squad: one grenade per hidden enemy at a time. |
| In an assault | The two assault-element units nearest the threat point and in range throw first. The rush starts on the throw: a unit's blast spares its own side (`CombatManager.Explode`). |
| Engine half | `UnitIntent.ThrowAt`. `ServerThrow` gains a second path for a unit with no target: the planned point, `targetInCover: true`, `targetSpeed: 0`. `ShouldThrow` is unchanged. The second path is `htn` only. |

### 5.5 The squad drill: fire and manoeuvre — requirements 1 and 5

| | |
|---|---|
| Squads | Board squads with ≥ 2 living units on foot, under an Attack or an objective (§5.6). A person's squads as well as the commander's: the drill is how an order is carried out (D1). Not Defend (the ring, React and "answer a call" cover it), Patrol, a plain Move or Build. Tanks, technicals and trucks in a squad are not elements and keep today's behaviour. |
| Cadence | Every 10 ticks per squad, staggered by squad index, after `CountSquads` (`UnitManager.cs:967`). Eight squads: at most one decision a tick. |
| Roles | `DrillRoles.Assign`, in unit id order. Gunners: base. Marksmen: overwatch. Riflemen, with a gunner: the assault, bounding in halves A and B when there are four or more, together below that. Without a gunner: half the riflemen are the base and the halves leapfrog, swapping base and bound each time. |
| Facts | `Contact` none · known · fired upon (any member, §4.1). `Base` none · moving · firing. `Range` from the leading element to the threat point: far (> 45 m) · bound (20–45 m) · assault (< 20 m), held 2 m past each edge. `Moving` none · A · B; `Arrived`. `Objective` none · approach · on. `HasGunner`. |
| Domain | `consolidate` [Objective=on ∨ no contact for 5 s]: posts on the point, or back to advancing. `react` [Contact=fired upon ∧ Base=none] `GoToGround`: every member to cover from the threat point, the base to its fire positions {≤ 4 s}. `assault` [Range=assault ∧ Base=firing] `Grenades` → `Rush`. `bound` [Contact≠none ∧ Base=firing] `Bound`: one element to its spots while the other holds and fires; on arrival, or after 8 s, the other. `establish base` [Contact≠none] `SetBase` {≤ 6 s, then bound regardless}. `move`: today's keep-pace advance. |
| Bound spots | Per moving unit: `CoverBoard.FindSpots` from it, against the threat point, `Toward` the objective (else the threat point), `MinProgressMeters` 5 m, search 12 m, no nearer the threat than 12 m; claimed as cover spots already are (`Unit.CoverSpot`). No spot: an open point 8 m on along the axis, offset by rank. |
| Directive | `DrillBoard[squad]`: phase, threat and suppression points, the moving element, each member's role and bound spot, the assault's go, the grenade claims. Units encode `DrillRole` and `DrillPhase` from it on their scan; operators read their points from it. |
| Give-ups | A drill on one threat point lasts ≤ 60 s; then the squad advances without one for 20 s (P4). H6's stall — a squad 50–60 m short of a node for a minute, killing a raider each time it respawned (HTN_BOTS.md §5.3, "fresh rounds") — is this case. |
| Pre-emption | Retreat pre-empts any role. React to fire does not pre-empt a moving element (its bound is to cover); it does pre-empt the holding one. A new order resets the drill. |

### 5.6 A Move to a control point — requirement 5

- `Encode` (`UnitManager.cs:1012`) marks an objective for a Move whose target is on a node, by the
  rule it uses for an Attack; `SenseObjective` (`UnitDomain.cs:577`) accepts Move as well.
- The point's three methods move from Attack into an Objective select that both kinds reach.
  Attack's chasing methods (engage focus, support, advance) stay Attack-only, so a Move still does
  not chase: it reacts (§5.1), drills (§5.5), takes and holds the point.
- A Move anywhere else keeps today's Obey, plus React to fire under its cap.
- **`OrderKind.Rush = 9`** is today's Move. `UnitBrain` treats it as Move, so legacy decides
  nothing new. `UnitOrder.IsIssuable` admits it; `StrategistController` arms it on `H` (unbound
  in `project.godot`) as X, C and B arm theirs; the HUD key list, the agent API's order names
  (`rush`) and the playtest's `ScriptCommand.OrderKinds` take it. The commander's Retreat, Refill
  and truck `Station` orders become Rush: they are moves today so as not to stop
  (HTN_BOTS.md §5.3).

### 5.7 Ground bots perceive fire — needed to measure requirement 1

`GroundFact.Suppressed`: a unit's round passed within `NearMissMeters` of the bot, or hit it, in
the last 1.5 s, from the same near-miss pass run for strategist rounds against ground characters.
In "fight from cover", a suppressed bot in its spot crouches and holds fire
(`BotSituation.InCover`'s crouch, which reloading uses) for at most `SuppressedMaxTicks` (3 s),
then rises regardless. In the open, `Suppressed` lets "fight from cover" pre-empt "engage" when a
spot exists. People and legacy bots: nothing.

### 5.8 Posture

Needed for a sniper's low-wall peek (§5.3) and for reloading or returning fire behind a 1.2 m wall
(§5.1, §5.2). `Unit.Crouched`, server-side, from `UnitIntent.Crouch`, on `UnitFlags` bit 7: the
13-byte unit record does not grow, and every earlier demo reads standing. Crouched: hitbox height
`CrouchHeightMeters` (1.1 m; 0 for a unit that cannot), eye 0.95 m, half move speed, no fire —
it stands to shoot, which is the pop-up. `VisibilityService` sees from the lower eye, so
concealment cuts both ways. Clients draw it from the bit. `CoverBoard`'s protect height for a
crouched unit is its crouched chest.

---

## 6. Phases

Estimates in solo-dev days, as HTN_BOTS.md §6. Every phase is under `--bot-ai htn` and behind
`--unit-tactics on|off` (T-D7), default off until T8.

### T0 — Sensing (1 day)

- `Projectile.Origin`, `ProjectileSegment.Origin`; `FireMemory`; `NearMiss` and its call in
  `ResolveSegment`; hits through `Damage`; `Doctrine` on `UnitDefinition` and the eight `.tres`;
  the three agent events and the harness's vocabulary for them; `--unit-tactics`
  (`LaunchOptions`, `GameModeDefinition`).
- Tests: `NearMissTests` (geometry, broad phase, victim excluded, fused rounds, determinism, 0 B);
  `FireMemoryTests` (window, kinds, seeded and bounded error, shooter id); `LaunchOptionsTests`.
- **Done when:** the debug HUD counts units fired upon and near misses in a `--listen` round;
  `unit ms` at 64 units moves by less than run-to-run noise; a scratch program puts the near-miss
  pass under 20 µs a tick with 64 units and 30 rounds in flight.

### T1 — React to fire, the shooter, Rush (1.5 days)

- §5.1; §4.1's shooter preference; `OrderKind.Rush` end to end; the commander's moves as Rush.
- Tests (`UnitHtnTests`): each row of §5.1; pre-emption of a running Obey and Advance; the Move
  cap and its reset on a hit; every new operator ending when its premise goes (the existing
  operator theory, extended); 0 B over 64 units × 7,200 ticks. `UnitBrainTests`: Rush is Move.
  Parse tests for `rush` in the agent command and the scenario script.
- Scenario `tactics_react_to_fire`: riflemen ordered to move past a scripted ground seat firing
  from 50 m, beyond their 45 m sensor. Claims: `unit_task[goal=TakeCoverFromFire]` ≥ 1 every
  seed; every unit that lives arrives (the cap); units lost below `--unit-tactics off` over the
  seeds.

### T2 — Grenades behind cover (1 day)

- §5.4.
- Tests (`GrenadeTests`): `TrySolveOver` over a 1.2 m wall and a 3 m block at 8–28 m, the high
  root's fuse margin, no arc; `GrenadePlan`'s candidates (hidden only, range, freshness) and claims.
  `UnitHtnTests`: the intent; legacy unchanged.
- Scenario `tactics_grenade_behind_wall`: a ground seat crouched still behind a low wall
  (`scripts/crouch_still.json`) 15 m from three riflemen under an attack order. Claims:
  `grenade_thrown[hidden=1]` ≥ 1 every seed; the seat killed in ≥ half. With
  `--unit-tactics off` no hidden throw happens — today's rule needs the head to show.

### T3 — Posture (1.5 days)

- §5.8 on the wire bit; the unit's render; crouching in `ReturnFire`, a gunner's reload and `Duck`.
- Tests: `UnitSnapshotCodecTests` (bit 7 round-trips; a record from before reads standing);
  `CoverTests` (crouched protect height); the crouched hitbox.
- **Done when:** `sandbag_cover` and the existing suite pass unchanged, and a display check shows
  a crouched unit behind a low wall hidden from a standing player.

### T4 — Sniper (2 days)

- §5.3; `CoverBoard.Concealed`, `TryFindPeek`; `UnitIntent.HoldFire`.
- Tests: `CoverTests` for both peek shapes, concealment against several threats, determinism, 0 B;
  `UnitHtnTests` for every row, the peek clock, displacement and the recent-spot rule.
- Scenario `tactics_sniper_overwatch`: a sniper (catalog id 5) defending near a block and a low
  wall; ground bots walking at it from 90 m (roster, with H6's idle seat in the far corner).
  Claims: `unit_task[goal=FireFromPeek]` ≥ 2; `events.kill.count` ≥ 1 (the sniper is the only
  strategist unit; a unit's kill reports weapon 0, `CombatManager.WeaponOnStream`, so the filter
  cannot name the rifle); the sniper alive at the end in ≥ half the seeds; each against
  `--unit-tactics off`.

### T5 — Gunner suppression; bots perceive it (1.5 days)

- §5.2 without the drill; §5.7.
- Tests: `UnitHtnTests` for `SuppressPoint` (bursts, the reserve, reloading in cover, nothing
  without a threat point); `GroundHtnTests` for `Suppressed` (crouch, the cap, pre-emption);
  `BotBrainTests` unchanged.
- **Done when:** in a bot round a gunner firing at a ground bot behind a wall keeps it crouched
  (the AI debugger shows `Suppressed` on the bot), and the bot rises after 3 s of continuous fire.

### T6 — The drill, and a Move to a control point (3 days)

- §5.5, §5.6. The drill in the AI debugger: a domain kind on the spectator channel (additive,
  AI_DEBUG.md §5), and world lines.
- Tests (`SquadDrillTests`): roles for each mix (gunner + 3, gunner + 6, 4 riflemen, 2, a sniper
  among them); every domain row; bound alternation; base first; the 6 s, 8 s and 60 s give-ups;
  consolidation; reset on a new order; determinism; 0 B over 8 squads × 7,200 ticks.
  `UnitHtnTests`: a Move on a node is an objective and a Move elsewhere is not; a Move objective
  never runs engage focus, support or advance.
- Scenario `tactics_move_to_node`: a scripted strategist seat moves a gunner and six riflemen to
  the middle node, held by ground bots behind its walls. Claims: the strategist captures the node
  in ≥ half the seeds; `squad_drill[phase=bound]` ≥ 1; units lost below `--unit-tactics off`'s
  over the seeds. Thresholds from drafts of more seeds than the file holds, as H6 set its.

### T7 — The commander uses them (1 day)

- Trains gunners at its pillboxes and snipers at its towers (`StrategistBrain`, numbers on
  `StrategistTraits`); at most one gunner per assault squad, attached as "escort armour" attaches
  tanks (HTN_BOTS.md §5.3); snipers join the garrison or a reinforcing squad, never the scout.
  The drill is the units', so this is composition only.
- Tests in `CommanderHtnTests` and `StrategistBrainTests`.

### T8 — Measure, switch on, document (1–2 days, then ongoing)

- Bot-only rounds as COVER.md §9 (five bots and the idle seat against one commander, 6 min, nine
  seeds), tactics on against off: units lost by tier; ground players killed; node-time held; time
  from an order on a node to its capture; sniper lifetime; grenades thrown, hidden and not.
- The `tactics_*` scenarios pass on and fail off on the claims that separate them; the existing
  suite passes under both.
- Default on. Update HTN_BOTS.md §5.2 (the Move rule), COVER.md §6 and §10 (peeks), NETCODE.md
  (the posture bit, Rush), AGENT_API.md (Rush, the three events), README (the key, the behaviour).

Total: about 12–13 days.

---

## 7. Verification

| Claim | Proof |
|---|---|
| A situation produces a given plan | Fact-vector → task-chain tests for every new row (`UnitHtnTests`, `SquadDrillTests`, `GroundHtnTests`) |
| Every new long-running operator ends when its premise does | The operator/premise theories, extended; the P3 case on the new branches |
| Nothing allocates on the tick | Zero-byte runs over the unit domain, the drill, `NearMiss`, `FireMemory`, the peek and arc searches, each with a `DefaultFactory` run that must allocate |
| Deterministic | Two-run trace equality with tactics on, near-miss pass and perception error included |
| Legacy is intact | `UnitBrainTests`, `BotBrainTests`, `StrategistBrainTests` unchanged; Rush is Move under legacy |
| The tactics are what helps | Each `tactics_*` scenario passes on and fails off; T8's rounds |
| It costs nothing noticeable | `unit ms` at 64 units, on against off; a scratch-program figure per phase |
| The guards can fail | Per phase, as H2–H6 did: remove an executing condition, a cap, a claim, the base-first rule; each fails a named test, then is restored |

---

## 8. Decisions needed

| # | Question | Recommendation |
|---|---|---|
| T-D1 | Does a Move to a control point become an objective? It reverses HTN_BOTS.md §5.2's "a Move is never second-guessed" for that case. | Yes, server-side in `Encode`, so a person, a policy's seat and a scenario script get the same behaviour; with `Rush` for "just go". The alternative — the client turning a right-click on a node into an Attack — leaves the agent API and scripts as they are and still marches every other Move into fire. |
| T-D2 | Do near misses count, or only hits? | Near misses. A unit that notices fire only when hit has been hit first. Cost: the T0 pass. |
| T-D3 | How well does a unit know where a shot came from? | Direction exactly, range to 10 %; never the shooter's current position. |
| T-D4 | A posture bit on the unit snapshot. | Yes: bit 7 is spare, nothing grows, earlier demos read standing. Without it snipers hide only behind blocks, of which `Test.tscn` has 8. |
| T-D5 | May a gunner suppress a position it has not itself seen? | Yes, from the side's memory ≤ 4 s and its squad's fired-from points, as a place to fire at, never a target to name (D4). |
| T-D6 | Ground bots perceive fire; people get nothing. | Yes (§5.7). Legacy bots unchanged (D5). |
| T-D7 | An A/B switch, `--unit-tactics on\|off`. | Yes, default off until T8: the only way to show on one build that a scenario fails without the tactics, as `--bot-ai legacy` did for H6. |

---

## 9. Risks

| Risk | Signal | Mitigation |
|---|---|---|
| Pinned for ever: react, suppression and drills keep a squad in cover | Time from an order on a node to its capture grows; `squad_drill` phases repeat | Caps on every phase and on a Move's react (§5.1, §5.5); give-ups with a retry delay (P4) |
| Orders feel ignored | Strategists report units "stopping" on a move | T-D1 limited to nodes; the react cap; Rush on a key; the drill visible in the AI debugger |
| Plan thrash at band edges | The debugger's history alternates goals each scan | Hysteresis on `FiredUpon` (its window), `Range` (2 m) and `Peek`; the drill on a 10-tick cadence |
| Too strong | The ground force loses bot-only rounds it used to win | Every number on `UnitPlanTraits` / `DrillTraits`; the switch |
| Privilege creep | Units act on what a person in their place would not know | Fired-from points with error, the unit's and squad's only; nothing into `ContactMemory`; no new rays |
| Cost | `unit ms` grows | A broad phase on the near-miss pass; box arithmetic only; one drill decision a tick |
| Wire | Old clients or demos draw units wrongly | Bit 7 was always 0; Rush is a new value of an existing byte; both recorded in NETCODE.md |
| Map dependence | `Test.tscn`'s 18 low walls and 8 blocks; peeks and bounds find little elsewhere | Every new branch falls through to today's behaviour when it finds no spot; the dig-in (COVER.md §7) adds walls |
