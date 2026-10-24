# Unit tactics — implementation plan

Status: **T0 built (§6); T1–T8 are plan. Decisions T-D1 to T-D7 taken (§8).** Planned from the
code at `6213d05`. Extends the unit HTN of [`HTN_BOTS.md`](HTN_BOTS.md) §5.2 and the cover of
[`COVER.md`](COVER.md).

Scope: strategist units that fight well without micro. Five requirements:

1. Heavy gunners suppress where enemies are or were, and anchor an assault by riflemen.
2. Snipers engage from range out of concealment: hide, pop out, fire, duck, relocate.
3. Every unit on foot takes cover from where it was last fired upon.
4. Every unit with grenades throws them at known enemies behind cover.
5. A Move to a control point is a goal — take the point — whose subgoals are 1–4 and moving as a
   team, not a march into fire.

And one control: **micro-autonomy**, a per-unit setting the strategist switches in game, observed
by the unit's planner as a fact. On, a unit does all of the above; off, it carries out orders as it
does today.

What does not change: legacy (`--bot-ai legacy`, the RL baseline, D5); `UnitBrain`'s
Idle/Moving/Engaging state, `Unit.Steer`, `WeaponSim`; the damage model; nothing about a player's
aim or view (NETCODE.md §10.8). Planning stays engine-free, allocation-free and deterministic
(HTN_BOTS.md §4.2).

---

## 1. What the code does today

| Requirement | Today | Gap |
|---|---|---|
| 1. Gunner suppression | `Suppression.Remember` / `TryPoint` (`Scripts/Sim/Suppression.cs`): a unit with `SuppressSeconds` (heavy gunner: 4 s) that loses sight of its own target keeps firing at the last aimed point (`UnitManager.SimulateUnit`, `UnitManager.cs:470`; `Suppress`, `:847`). | A reflex only. No fire on a position another unit saw, no role in its squad, nothing waits on its fire. The commander never trains gunners or snipers: neither appears in `Commander.cs` or `BotStrategist.cs`. Ground bots do not perceive fire: no suppression fact in `GroundDomain.cs`, `BotPilot.cs` or `BotBrain.cs`, so suppression costs a bot only the rounds that hit it. |
| 2. Sniper concealment | The rifleman's domain and branches. `sniper.tres`: 70 m sensor and engage range, 80 hp; the marksman rifle fires 30 rpm. | No notion of being seen. `CoverBoard` spots are "fight over a low wall" or "hide behind anything" (builders); COVER.md §10: no peeking round a block. Units cannot crouch: `Unit.Hitbox` is the definition's full height (`Unit.cs:249`), so a unit behind a 1.2 m wall always shows its head. |
| 3. Cover from the shooter | `UnitManager.Damage` (`:1547`) sets `Unit.LastDamagedTick`; `attackerOwnerId` is carried "only so that the agent API's `unit_lost` record can name who did it". `UnitFact.UnderFire` (hit ≤ 2 s) is read by Build only. | No position for a shot. Near misses are not seen. Cover is sought from the unit's own target, the nearest remembered contact (builders) or the ring's threat (`SenseCover`, `:1230`), never from the shooter. A unit shot by someone it cannot see stands in the open. |
| 4. Grenades | `ServerThrow` (`:709`): at the current target only, while Engaging, when `suppressing \|\| AimsHigh` or the target is slow. Low arc only (`GrenadeSim.TrySolve`). | Never at a contact the unit cannot see, so never at a player crouched fully behind a wall (COVER.md §3: a crouched head does not show). No check that the arc clears the wall. No coordination: every unit in range may throw at one player. |
| 5. Move to a control point | Right-click defaults to `OrderKind.Move` (`StrategistController.cs:140`). The unit domain's "obey" follows the order, firing on the walk. `UnitFact.Objective` is set only for an Attack naming no contact (`SenseObjective`, `UnitDomain.cs:577`). HTN_BOTS.md §5.2: "A `Move` order is never second-guessed". | A person's move to a node walks the straight route through whatever is shooting at it. |
| Control | None. A unit's behaviour depends on the server's `--bot-ai` and on who issued its order (`UnitStance`, D1). | A strategist who wants a unit to do exactly what it is told cannot say so. |

---

## 2. Rules

HTN_BOTS.md §4.2 holds. Added:

1. **The order is the goal; the unit and its squad choose the subgoals.** A Move to a control point
   is the goal "take the point" (T-D1). Any other Move gains "react to fire", under a cap.
2. **Micro-autonomy is the strategist's, per unit, and the planner observes it.** It is a fact in
   the unit's world state (§5.9). Every method this plan adds requires it on, and every operator
   it adds holds only while it is on, so switching it off ends a running tactic on that tick and
   switching it on lets one pre-empt (HTN_BOTS.md §3.4, P2, P3).
3. **Every deviation is bounded.** A tactic that leaves the order's line has a distance bound, a
   time cap, and a give-up that turns it off for a while (P4). A squad pinned for ever is the most
   likely failure of this plan (§9).
4. **A unit knows where a round came from, not who fired it or where they are now**, with an error
   of 10 % of range (T-D3). That knowledge is the unit's and its squad's. It does not enter the
   side's `ContactMemory`, so the commander, the fog and the strategist's observation are unchanged
   (D3, D4).
5. **No status on people.** A person is suppressed by rounds and nothing else (NETCODE.md §10.8). A
   ground bot gets a perception fact (T-D6, §5.7).
6. **No new rays.** Near misses are analytic (segment against capsule). Concealment, peeks, bound
   spots and grenade arcs are `CoverBoard` box arithmetic. A preferred shooter replaces the
   acquisition search's ray when its line is clear, as the squad focus does (HTN_BOTS.md §5.2).

---

## 3. Architecture

```
 Commander HTN (bot strategist)          person / policy seat / scenario script
   │ + trains gunners and snipers,                 │  + micro-autonomy per unit, and the
   │   one gunner per assault squad (T7)           │    team's default for new units (§5.9)
   ▼  ServerIssueOrder                             ▼  ApplyOrder · ClientSetAutonomy
 SquadBoard  (one order batch = one squad; unchanged)
   │
   ▼  NEW  squad drill, per board squad, every 10 ticks                   NEW  FireMemory, per unit
   │       threat point · roles · phase · bound spots · grenade claims        ▲ hits and near misses
   ▼  DrillBoard                                                              │ from CombatManager
 Unit HTN  (§5.2 + the facts and branches of §5, gated by MicroAutonomy) ─────┘
   ▼  UnitIntent  (+ FireAt, ThrowAt, HoldFire, Crouch)
 UnitManager: Steer · ServerFire (+ at a point) · ServerThrow (+ at a remembered point) · posture
```

| Piece | Where | What |
|---|---|---|
| Projectile origin | `ProjectileSim.cs` (`Projectile`, `ProjectileSegment`) | `Origin`: where the round left the muzzle. Server-side; the spawn message already carries it (`ProjectileCodec`). |
| `FireMemory` | `Scripts/Sim/FireMemory.cs` (T0) | Per unit (`Unit.Fire`): the worst round this window (near miss · hit), the perceived origin and the shooter of the latest, its tick, rounds in the window. Pure functions in `FiredUpon`, as `Suppression` is for its memory. |
| `NearMiss` | `Scripts/Sim/NearMiss.cs` (T0), called through `UnitManager.NoteRound` / `NoteBlast` (`UnitManager.Fire.cs`) from `CombatManager.ResolveSegment`, `ResolveFused`, `Detonate` and `ResolveMelee` | A round's chord, up to what it hit, against every enemy body near it, behind a per-chord box (`NearMiss.Chord`). Feeds `FireMemory` (units, T0) and the ground sensor (bots, T5). |
| `Doctrine` | `UnitDefinition` export, mirrored in `UnitTraits` | `Rifleman`, `Gunner`, `Marksman`, `None` (builders, vehicles, trucks). Explicit in each `.tres`, not inferred from `SuppressSeconds` or range. |
| `CoverBoard` additions | `Scripts/Sim/Cover.cs` | `Concealed` (no remembered threat's eye sees a point); `TryFindPeek` (a hide spot and a fire spot beside it); `CoverQuery.Toward` and `MinProgressMeters` (spots that gain ground). |
| Posture | `Unit.cs`; unit snapshot v2 (§5.8); `UnitDefinition.CrouchHeightMeters` | Units on foot crouch: lower hitbox and eye, slower, no fire. Replicated as a field of its own. |
| Squad drill | `Scripts/Sim/Htn/DrillDomain.cs`, `SquadDrill.cs` (new) | A FluidHTN domain per board squad: the squad's half of "take the point" and of an attack. Roles are a plain function (`DrillRoles.Assign`), as the ground coordinator's are. Writes a `DrillDirective` per squad. |
| `GrenadePlan` | `Scripts/Sim/Grenades.cs` | Choosing a remembered contact behind cover; `TrySolveOver`: the low arc, else the high one, checked against `CoverBoard` boxes. |
| Micro-autonomy | `Unit.cs` (`Autonomy`), `UnitFact.MicroAutonomy`, `UnitManager` requests, `StrategistController`, `StrategistHud`, `AgentCommand.cs`, `tools/Gdpyr.Playtest/Scenario.cs`, `LaunchOptions`, `GameModeDefinition` | §5.9. |
| Unit domain | `Scripts/Sim/Htn/UnitDomain.cs` | The facts and branches of §5. |
| Engine half | `Scripts/Rts/UnitManager.cs` | Encodes the new facts; carries out the new intent fields. |
| Ground domain | `GroundDomain.cs`, `BotPilot.cs` | `GroundFact.Suppressed` (§5.7). |
| Debugger | `Scripts/Sim/AiDebug/` | The drill as a domain kind; new facts and goals in `HtnSchema` / `AiGoals`; world lines for threat points, fire lines and bound spots. |
| Agent events | `Scripts/Sim/Agent/AgentEvents.cs` | `grenade_thrown` (T0: thrower, its catalog id, whether the target was hidden); `unit_task` (a unit started one of §5's goals, from T1) and `squad_drill` (a drill phase change, from T6), each added with the first thing that emits it. For scenarios; additive. |

---

## 4. The threat picture

### 4.1 Fired upon

| | |
|---|---|
| Hits | In `ResolveSegment`, every chord goes to `UnitManager.NoteRound` with the round's `FireSource` (origin, owner, definition, id) and the unit it hit, if any: that unit records a hit. A round that went off where it stopped (the launcher's) is a hit on every unit its blast reached. A grenade's blast (`Detonate`) is a hit on every unit within its reach. Damage itself is unchanged: `Damage` still sets `LastDamagedTick`. |
| Near misses | The same call: the chord from `From` to where the round stopped — the contact point, else the chord's end — against every unit of the other side on foot whose capsule axis it passes within `NearMiss.Meters` (1.5 m) of, the victim excluded (T-D2). Broad phase: one box per chord, grown by 1.5 m (or a blast's reach plus the widest unit's 2 m radius), against each unit's feet, before any capsule is built. A grenade that comes to rest (`ResolveFused`) is a near miss on every unit within its blast's reach (7 m). |
| Melee | `ResolveMelee`: the swing from the attacker's eye to what it reached, by the same rules. |
| Perceived origin | The true origin moved along the line from the unit's eye by up to `FiredUpon.ErrorFraction` (0.1) of the range, in or out — the direction exact, the range to 10 % (T-D3) — seeded by `Spread.Seed(unit owner, definition, round id)`. |
| Window | `FiredUpon.WindowTicks` (3 s) after the latest round; rounds inside it keep it open, the worst kind in it is kept, and one after it starts a new window. `UnitFact.FiredUpon` = none · near miss · hit, read from it every tick (T1). Build's `UnderFire` (hit within 2 s, from `LastDamagedTick`) is left as it is rather than folded into `FiredUpon = hit`: every builder reads it, micro-autonomy on or off, and a 3 s window would change what an off builder does. |
| Shooter | `AcquireTarget` tries the shooter after a named target and before the squad focus, by the focus's rule — under micro-autonomy, while the window is open: one ray, replacing the search's when clear; when it is blocked, neither the focus nor the search pays for it again. Returning fire at the shooter needs a line to them; without one, §5.1 fires at the place. |

Recording runs whatever a unit's micro-autonomy, under `--bot-ai htn` only (legacy plans nothing,
so the pass would be cost with no reader); only planning reads it.

### 4.2 Where enemies are and were

One list per squad, rebuilt on the drill's cadence, is what gunners suppress, grenades are thrown
at and the drill moves against:

- every contact the side remembers (`ContactMemory`): at its live position while any friendly sees
  it, at its last-known position as a ghost until the side forgets it (`SimConfig.GhostLifetimeTicks`,
  8 s);
- every fired-from point in its members' `FireMemory`, kept on the `DrillBoard` for
  `FiredFromMemoryTicks` (8 s, the same lifetime) after the round.

That is T-D5: a gunner may fire at any place an enemy is seen or was last seen. A contact's trail
of older positions is not kept: an older position is a worse guess than the last one.

The **threat point** is the entry within 60 m of the squad's centroid or of the line to its
objective with the most members fired upon from within 5 m of it, then the nearest the objective
line, then the lowest owner id. The **suppression point** of any entry is its position raised to
`SuppressAimMeters` (1.3 m) above its ground: rounds aimed there strike the top of a 1.2 m wall or
the head over it.

A ghost as a place to fire at, never as a target to name, is within D4; units already walk to
ghosts (HTN_BOTS.md §5.2, "support").

---

## 5. Behaviours

### 5.0 The goal and its subgoals (T-D1)

"Take the control point" is the order's task when the order is a Move, or an Attack naming no
contact, whose point is on a resource node. It decomposes at two layers: the squad's drill picks
the subgoal for the squad, and each unit's domain carries out its part.

| Subgoal (squad drill, §5.5) | When | What each unit does (unit domain) |
|---|---|---|
| Hold the point | on the point, or nothing in contact for 5 s | hold the point from cover · hold the point (existing) |
| Get into cover | fired upon, nothing yet firing back | React to fire: to cover from the fired-from point, return fire (§5.1) |
| Assault | the leading element within 20 m of the threat point, the base firing | grenades at the hidden contact (§5.4), then take the point |
| Bound | in contact, the base firing | base: suppress (§5.2) · moving element: to the next cover towards the point · overwatch: peek and fire (§5.3) |
| Establish a base of fire | in contact | the gunner, or a rifle half, to fire positions (§5.2); the rest hold in cover |
| Approach | no contact | take the point, keeping pace with the squad (existing) |

A unit alone, or a squad with fewer than two units on foot whose micro-autonomy is on, has no
drill: it reacts, throws, suppresses or peeks by its own doctrine, and takes the point.

### 5.0.1 The unit domain

New branches in bold; `{…}` are executing conditions. Every new method also requires
`MicroAutonomy=on` and every new operator holds only while it is on and while its order holds
(HTN_BOTS.md §5.2, "Order").

```
unit
├─ Retreat                      unchanged
├─ **Grenade**                  [Grenade ∧ Doctrine≠none ∧ Contact≠own ∧ SquadPhase≠falling back
│                                ∧ Order∉{Build,Embark,Unload}]  ThrowGrenade {the same}            §5.4
├─ **React to fire**            [FiredUpon≠none ∧ Contact≠own ∧ FireCover≠none ∧ ¬Bounding
│   │                            ∧ SquadPhase≠falling back ∧ Order∉{Build,Embark,Unload}]          §5.1
│   ├─ return fire from cover   [FireCover=in]  ReturnFire {FiredUpon≠none, FireCover=in}
│   └─ get to cover             TakeCoverFromFire {FiredUpon≠none, FireCover≠none}
├─ Resupply                     unchanged
├─ Build                        "hide behind cover" hides from the fired-from point when there is one
├─ **Marksman**                 [Doctrine=marksman ∧ (Order∈{Attack,Defend,None} ∨ Objective)]       §5.3
├─ **Take the point**           [Objective]                                                          §5.0, §5.6
│   ├─ wait for squad
│   ├─ **drill**                [DrillRole≠none ∧ DrillPhase≠approach]                              §5.5
│   └─ hold the point from cover · hold the point · take the point     moved here from Attack
├─ Attack                       today's, for an attack not on a point; **drill** above engage from cover
├─ Defend Zone                  unchanged; React to fire covers a ring shot at from beyond its sensor
└─ Recon · Obey · Carry         unchanged
```

With micro-autonomy off, every bold branch fails its condition and the tree is today's, except that
an objective under a Move is not formed either: `Encode` marks a Move objective only for a unit
whose micro-autonomy is on, so an off unit on a Move obeys as it does today.

### 5.1 React to fire — requirement 3

As built in T1 (`UnitDomain.cs`'s "react to fire" select, below Retreat and above Resupply;
`UnitManager.SenseFireCover`):

| | |
|---|---|
| Who | A unit with a doctrine (`UnitTraits.Doctrine` not `None`: not a builder, a vehicle, a supply truck or the dropship), its micro-autonomy on. Riders and aircraft plan nothing. |
| Facts | `FiredUpon` (§4.1, every tick); `FireCover`: `CoverState` against the fired-from point, the spot found on the scan and banded every tick as `Cover` is; `ReactSpent`, the cap below. |
| When | Fired upon, with a spot, the cap not spent, its board squad not falling back (§5.6), its order not a build, a walk to a vehicle or an unload — and nothing of its own to fight from cover: no target, or an order that shoots on the walk (a move or a patrol). A unit under an attack or a defend order that can see its target fights it from cover against it, as before (COVER.md §6). |
| Spot | A low wall it can fire over within 10 m (`ReactCoverSearchMeters`), at any range from the shooter, else anything that hides it within 6 m (`ReactHideSearchMeters`). Under an order that goes somewhere — a move, a patrol, an attack — it must be on the way (`CoverQuery.HasToward`): no further from where the order goes than the unit is plus 3 m, and within 6 m of the line to it. Only behind a box the unit is not already in front of (`CoverQuery.StaySameSide`): a unit between a wall and the shooter would reach the wall's far face only by walking round its end, down the line the fire comes along. One spot a unit: while it reacts, the fire spot is its claimed spot, and the fight's is not looked for. A unit that must stand on a point (`ControlPoint.Holds`) does not leave it for one; T6 revisits that. |
| Operators | `TakeCoverFromFire`: to the spot without stopping. `ReturnFire`: in it, holding. Both fire at the fired-from point for `ReturnFireTicks` (2 s) after each round that reaches it (`UnitIntent.FireAt`), when it is inside the unit's engage range — a shooter beyond 40 m is hidden from, not answered, and since T3 the planner says so too (`FireBack` measures it), rather than leaving the engine to refuse — and never throw a grenade at that point: it is a place, not a player, and nobody's focus. From T3, `ReturnFire` crouches whenever it is not firing back and has no target of its own (§5.8). |
| Shooter | `AcquireTarget` tries whoever last fired at it before the squad focus (§4.1): a unit that can see its shooter shoots them, and then fights them as its target. |
| Ends | The window (3 s after the last round); a target of its own under an order with a fight of its own; the spot no longer hiding it; its squad falling back; a build order; micro-autonomy off — each an executing condition, each a test. |
| Cap | Under an order that goes somewhere, `ReactMaxTicks` (8 s) in all since the order, the windows' tails included; then `ReactSpent` until the next hit (`FireMemory.LastHitTick`) or a new order, so the order still arrives. Under a defend order or none there is no cap: holding in cover while fire keeps coming is what holding is. The plan had the cap on a move only; an attack with no drill yet (T6) would otherwise be pinned for ever by one shooter it cannot see. |
| Builder | Under micro-autonomy, "hide behind cover" hides from the fired-from point when there is one, else from the nearest contact, as before. |
| Not | A squad falling back. A drill's moving element waits for T6, which adds it. |

### 5.2 Heavy gunner: base of fire — requirement 1

| | |
|---|---|
| Targets | Any entry of §4.2 within its 45 m engage range that it has a line to over cover — where an enemy is seen now, by any friendly, or was last seen, or fired from (T-D5). A target it sees itself it engages as today. |
| Alone | `SuppressPoint` fires `SuppressBurstTicks` (1 s, ≈ 9 rounds at the LMG's 550 rpm) every `SuppressCycleTicks` (2 s) at the suppression point of the entry nearest it, then the next. Today's 4 s reflex stays. |
| Ammunition | The 150-round belt is ≈ 33 s of that cycle. Below `BeltReserveRounds` (30) it fires only in a drill's bound and assault phases. It reloads (6 s) in cover, crouched. |
| In a drill | Always the base element, at the squad's threat point. `TakeFirePosition` is the squad's first move after contact: a low-wall spot 20–40 m from the threat point it can fire over at the suppression point, preferring a bearing ≥ 30° off the assault's axis. The drill does not bound until the base is firing, or 6 s have passed (§5.5). |
| Lift and shift | Unit rounds pass through friendly units (`UnitManager.QuerySegment` skips the shooter's team), so lifting fire is about tempo, not safety: once the assault element is within 8 m of the point the gunner shifts to the next entry within 25 m beyond it, else bounds onto the objective. |
| Leading the assault | The gunner sets the tempo: its position is the first move after contact, its fire is the trigger for the bounds, and the commander builds assault squads round gunners (T7). A drill whose gunner dies falls back to a rifle half as its base. |

### 5.3 Sniper: concealment, pop out, relocate — requirement 2

| | |
|---|---|
| Threats | §4.2's entries within 90 m. |
| Concealed | For every threat, a `CoverBoard` box crosses the line from the threat's eye (1.5 m) to the unit's head at the posture it would hold there. Range alone is not concealment: a unit cannot tell a bot with a 45 m sensor from a person with a scope. |
| Peek pair | `CoverBoard.TryFindPeek`: a concealed hide spot, and a fire spot within 2.5 m of it with a clear box line to the target's last-known eye. Behind a block, the fire spot is one slot past its edge. Behind a low wall, it is the same spot, crouched then standing (§5.8). Search: 35–65 m from the primary threat, within 25 m of the sniper, inside its leash under Defend. Best: shortest walk, then fewest threats that see the fire spot, then the widest angle off its squad's axis (a flank). |
| Facts | `Doctrine`; `Peek` none · hidden · exposed; `PeekTarget` (a contact in band the fire spot sees); `Standoff` (a threat inside 30 m); `Burned` (fired upon while exposed, or two peeks from one pair). |
| Domain | `displace` [Burned ∨ Standoff] `Relocate`: to a pair ≥ 8 m from the last fire spot and ≥ 6 m from any it fired from in the last 20 s. `engage` [Peek=hidden ∧ PeekTarget ∧ hidden ≥ `MinHideTicks` (2 s)] `PopOut` → `FireFromPeek` {≤ `PeekMaxTicks` (4.5 s), ≤ `ShotsPerPeek` (2; the rifle fires every 2 s)} → `Duck`. `hide` [a pair exists] `ToHide`, holding fire beyond 30 m on the way (`UnitIntent.HoldFire`). No pair: fall through to the order's branches, as a rifleman. |
| Targets | The squad's focus when in band; else a contact a friend is engaging (it is looking the other way); else the slowest, the weakest, the lowest id. |
| In a drill | Overwatch: not an element. Its peeks are timed to the bound phases, so it fires while an element moves. |

### 5.4 Grenades at known enemies behind cover — requirement 4

| | |
|---|---|
As built in T2 (`Grenades.cs`'s `GrenadePlan`, `GrenadeClaims` and `GrenadeSim.TrySolveOver`;
`UnitManager.Grenades.cs`; the unit domain's "grenade" select, below Retreat and above React to
fire):

| | |
|---|---|
| Who | A unit with a doctrine and a grenade ready (`GrenadeSim.AiReady`: one left, 6 s since its last), its micro-autonomy on, no target of its own, its order not a build, a walk to a vehicle or an unload, its squad not falling back. The plan had any unit on foot; a builder's micro-autonomy means nothing on the card (§5.9), so it does not throw on its own either. |
| Candidates | On the unit's scan: every ground-force player the side remembers (`ContactMemory`) seen ≤ 3 s ago (`GrenadePlan.FreshTicks`) and still alive — as a gunner stops firing at a dead target (`Suppress`) — and the unit's own fired-from point while its window is open; 6–28 m from its eye; behind a `CoverBoard` box — the line from its eye to 0.5 m above where the enemy was recorded crosses one (`HiddenAimMeters`: a crouched player's recorded position is its origin, 0.3 m off the ground, so the plan's chest is over a 1.1 m wall). A fired-from point within 3 m of a contact in the list is that contact. On the way first (within 6 m of the line to where its order goes), then the nearest. Squad-mates' fired-from points wait for T6's `DrillBoard` (§4.2). |
| Arc | `GrenadeSim.TrySolveOver`: today's low arc, flown tick by tick through the projectile integrator against `CoverBoard.FirstHit`; it comes to rest against the first box it strikes, else where it comes down, and the throw is made only if that is within `KillReach` of the enemy, measured flat: the blast's lethal distance from a full-health body (140 × (1 − d/7) ≥ 100 → 2 m) against a 0.5 m capsule ending at the feet, 2.45 m. A blast is not stopped by walls (`CombatManager.Explode`), so a grenade that stops against the near face of the wall an enemy crouches behind kills them, and one that stops against a 3 m block between them does not. **No high arc**: at 18 m/s the high root is still in the air when the 3 s fuse runs out at every range `GrenadeTests` tries, 6 to 28 m, so the plan's fallback never lands. Seen from above a flight is a straight line (drag acts along the velocity), so the boxes near it are gathered once (`CoverBoard.Near`) and each chord asks only those. A unit that finds no arc does not look again for a second (`Unit.NoArcUntilTick`). |
| Claims | `GrenadeClaims`, one table per side: one claim per contact, per 3 m cell for a fired-from point, for 4 s (`GrenadePlan.ClaimTicks`) after the throw — one grenade per hidden enemy at a time. Checked on the scan and again at the throw. The plan put them on the squad's `DrillBoard`; that waits for T6, and a side-wide table is the stricter of the two. |
| Planner | `UnitFact.Grenade`, set on the scan; `ThrowGrenade` stands for one tick with `UnitIntent.ThrowAt` and `ThrowPoint`, and the engine throws that tick and clears the fact, thrown or not (`UnitContext.Threw`). Above React to fire: a unit running for cover throws on the way. `unit_task` goal 20. |
| Engine half | `UnitManager.ThrowAtHidden`, a second path beside `ServerThrow`'s (unchanged): from where the unit stands that tick, `ShouldThrow` with `targetInCover: true`, `targetSpeed: 0`, the claim, then `TrySolveOver` again; `grenade_thrown` with `hidden` 1. `htn` only, as every plan is. |
| In an assault | T6. |

### 5.5 The squad drill: fire and manoeuvre — requirements 1 and 5

| | |
|---|---|
| Squads | Board squads with ≥ 2 living units on foot whose micro-autonomy is on, under an Attack or an objective. A person's squads as well as the commander's: the drill is how an order is carried out (D1). Not Defend (the ring, React and "answer a call" cover it), Patrol, a Move that is not an objective, or Build. Units whose micro-autonomy is off, and tanks, technicals and trucks, are not elements and keep today's behaviour. |
| Cadence | Every 10 ticks per squad, staggered by squad index, after `CountSquads` (`UnitManager.cs:967`). Eight squads: at most one decision a tick. |
| Roles | `DrillRoles.Assign`, in unit id order. Gunners: base. Marksmen: overwatch. Riflemen, with a gunner: the assault, bounding in halves A and B when there are four or more, together below that. Without a gunner: half the riflemen are the base and the halves leapfrog, swapping base and bound each time. |
| Facts | `Contact` none · known · fired upon (any member, §4.1). `Base` none · moving · firing. `Range` from the leading element to the threat point: far (> 45 m) · bound (20–45 m) · assault (< 20 m), held 2 m past each edge. `Moving` none · A · B; `Arrived`. `Objective` none · approach · on. `HasGunner`. |
| Domain | `hold the point` [Objective=on ∨ no contact for 5 s]: posts on the point, or back to approaching. `get into cover` [Contact=fired upon ∧ Base=none] `GoToGround`: every member to cover from the threat point, the base to its fire positions {≤ 4 s}. `assault` [Range=assault ∧ Base=firing] `Grenades` → `Rush`. `bound` [Contact≠none ∧ Base=firing] `Bound`: one element to its spots while the other holds and fires; on arrival, or after 8 s, the other. `establish base` [Contact≠none] `SetBase` {≤ 6 s, then bound regardless}. `approach`: today's keep-pace advance. |
| Bound spots | Per moving unit: `CoverBoard.FindSpots` from it, against the threat point, `Toward` the objective (else the threat point), `MinProgressMeters` 5 m, search 12 m, no nearer the threat than 12 m; claimed as cover spots already are (`Unit.CoverSpot`). No spot: an open point 8 m on along the axis, offset by rank. |
| Directive | `DrillBoard[squad]`: phase, the §4.2 list and its threat point, the moving element, each member's role and bound spot, the assault's go, the grenade claims. Units encode `DrillRole` and `DrillPhase` from it on their scan; operators read their points from it. |
| Give-ups | A drill on one threat point lasts ≤ 60 s; then the squad approaches without one for 20 s (P4). H6's stall — a squad 50–60 m short of a node for a minute, killing a raider each time it respawned (HTN_BOTS.md §5.3, "fresh rounds") — is this case. |
| Pre-emption | Retreat pre-empts any role. React to fire does not pre-empt a moving element (its bound is to cover); it does pre-empt the holding one. A new order resets the drill. |

### 5.6 A Move to a control point — requirement 5

- `Encode` (`UnitManager.cs:1012`) marks an objective for a Move whose target is on a node, by the
  rule it uses for an Attack, for a unit whose micro-autonomy is on; `SenseObjective`
  (`UnitDomain.cs:577`) accepts Move as well as Attack.
- The point's three methods move from Attack into the "take the point" select that both kinds
  reach (§5.0.1). Attack's chasing methods (engage focus, support, advance) stay Attack-only, so a
  Move does not chase what it sees off the point: its subgoals are cover, suppression, grenades and
  the team's bounds, all towards the point.
- A Move anywhere else keeps today's Obey, plus React to fire under its cap.
- **No new order kind.** The first cut of this plan had a `Rush` order for "just go". Micro-autonomy
  off (§5.9) is that, for a person; for the commander, whose retreats are moves so as not to stop
  (HTN_BOTS.md §5.3), units in a squad whose board phase is falling back do not react, and the
  commander's Refill move sets that phase as Retreat's does. Its supply trucks take no part in
  React.

### 5.7 Ground bots perceive fire — needed to measure requirement 1

`GroundFact.Suppressed`: a unit's round passed within `NearMissMeters` of the bot, or hit it, in
the last 1.5 s, from the same near-miss pass run for strategist rounds against ground characters.
In "fight from cover", a suppressed bot in its spot crouches and holds fire
(`BotSituation.InCover`'s crouch, which reloading uses) for at most `SuppressedMaxTicks` (3 s),
then rises regardless. In the open, `Suppressed` lets "fight from cover" pre-empt "engage" when a
spot exists. People and legacy bots: nothing (T-D6).

### 5.8 Posture, and the unit snapshot v2

Needed for a sniper's low-wall peek (§5.3) and for reloading or returning fire behind a 1.2 m wall
(§5.1, §5.2). As built in T3:

| | |
|---|---|
| Body | `UnitBody` (`Scripts/Sim/UnitBody.cs`, engine-free): height, eye, hitbox, protect height and speed by posture. `UnitDefinition.CrouchHeightMeters` and `CrouchEyeHeightMeters`, 1.0 m and 0.85 m for the four units on foot, nothing for a vehicle or an aircraft, which stand. The plan's 1.1 m is the sandbag wall's own height: a standing player's line over the wall's back edge still clips the crown of a 1.1 m capsule 0.6 m behind it, at every range from 5 to 60 m, and 1.0 m is clear at all of them (`UnitBodyTests`). |
| Crouched | `Unit.Posture`, set every tick from `UnitIntent.Crouch` (legacy plans nothing, and stands). The hitbox and the collider are the crouched capsule, the eye is 0.85 m, it walks at half speed, and it does not fire or throw: the weapon still steps, so a reload runs on. `VisibilityService` and every line of sight start at `EyePosition`, so a crouched unit sees nothing over a wall it is hidden behind. |
| When | `ReturnFire`, whenever it is not firing back (more than 2 s since a round, or a shooter beyond its engage range) and has no target of its own. Under micro-autonomy, any unit in its spot behind cover — `HoldCover`, `HoldPointCover`, `ReturnFire` — while its weapon reloads (`UnitContext.SenseWeapon`, a field, not a fact: nothing is planned on it). The sniper's `Duck` comes with T4. |
| Cover | `UnitCoverQuery` protects the body the unit has now — a crouched unit is behind cover behind a box that would leave it standing in the open — and fires from its standing eye, as it stands to shoot. |
| Client | `Unit.TakePosture` on each record, the collider and the drawing reshaped from the feet; demo playback goes through the same call. The posture is applied when the record is decoded, a snapshot interval before the drawn transform reaches its tick, as the state already is. |

The record is redesigned rather than squeezed (T-D4). Today's one flag byte packs a unit's body and
an aircraft's flight into eight bits with one spare (NETCODE.md §6.1); posture as an enum with room
does not fit, and the flight bits mean nothing to a unit on foot. v2 splits it:

| Byte | v1 (13 B) | v2 (14 B) |
|---|---|---|
| flags | state (2) · team (1) · aboard (1) · flight phase (2) · cannot land (1) · spare (1) | — |
| body | — | state (2) · team (1) · aboard (1) · posture (2: standing, crouched, two reserved) · spare (2) |
| flight | — | phase (2) · cannot land (1) · spare (5) |

The other fields are unchanged. Cost: 64 B more for a full field of 64, ≈ 1.3 KB/s per client at
20 Hz, against NETCODE.md §7's ≈ 20 KB/s downstream. `UnitSnapshot` and its `SizeBytes`,
`UnitFlags` (split into `UnitBodyFlags` and `UnitFlightFlags`; `PackHealth`, which `Structure` also
uses, stays), `UnitSnapshotCodec` and `Unit.ApplyRemoteState` (`Unit.cs:483`) change together;
`DemoFormat.Version` goes from 2 to 3, and
`DemoHeader`'s version check (`DemoHeader.cs:84`) refuses an older demo with its reason rather
than misreading it. No translation layer, as `DemoFormat.cs` already says of bumps.

### 5.9 Micro-autonomy — the strategist's switch (T-D7)

| | |
|---|---|
| State | `Unit.Autonomy`: `MicroAutonomy.Off` · `On`, server-side, per unit. Kept under legacy too, where nothing reads it. |
| In the planner | `UnitFact.MicroAutonomy`, written every tick from the unit, as `Order` is. Every method of §5 requires `On`; every operator of §5 holds while `On` (rule 2). It is a fact rather than a flag outside the planner so the debugger shows it, the domain tests cover it, and switching it is a replan like any other change. |
| What Off is | The unit domain as it is at `6213d05`: the order carried out as given, with H3–H6's and COVER.md's methods — keeping pace, focus fire, posts, fighting from cover on an attack or defend, taking a point on an attack. Not an element of a drill, never an objective under a Move, no React, no peeks, no deliberate suppression, no grenade at a remembered contact. Retreat and Resupply still follow `UnitStance` (D1), which is about who gave the order, not about this. |
| New units | Start with the team's default: the game mode's `UnitAutonomy` (off until T8), overridden by `--unit-autonomy on\|off`, and set in game by either strategist (two share the army; the last to set it wins, and both are shown it). A new round goes back to the default the server started with, whatever the last round set: one round's settings must not leak into the next's, which is what made scenario seeds depend on each other in HTN_BOTS.md §5.3. |
| In game | The selection card shows **Tactics: Auto / Direct** for the selection (**Mixed** when it differs) and toggles it; `H` (unbound in `project.godot`) toggles the selection; the HUD has the team default for new units beside the points. |
| Wire | `ClientSetAutonomy(int[] unitIds, byte mode)` and `ClientAutonomyDefault(byte mode)`, C→S, reliable, per click; the server checks the sender is a strategist and the units are its side's, as `ApplyOrder` does, counting what an honest client cannot send in `RejectedOrders`. `ServerAutonomyState`, S→C, reliable, **to strategist peers only**, whole — the default, then every living unit whose mode is not it (`AutonomyStateCodec`, 3 B + 3 B a unit) — on the barracks report's 5 Hz tick to every strategist peer not yet sent the current revision, so a peer that takes the chair gets it within 0.2 s. Not in the unit snapshot, which every ground client receives unfogged: a setting is an order, and orders are not on the wire to anyone (NETCODE.md §6.3). |
| Agent API | Strategist commands `autonomy {units, mode}` and `autonomy_default {mode}`; `welcome` says `unit_autonomy`, the server's default. The unit block of the strategist observation gains the mode in T8, which changes the published layout: `gdpyr-agent-obs-2` → `-3` (AGENT_API.md §4). |
| Scenarios | A spawn's `"autonomy": "on" \| "off"`; a strategist script's `autonomy` and `autonomy_default` commands; a scenario's `"unit_autonomy"`, which the harness passes to the server it starts as `--unit-autonomy`, as it does `bot_ai` — and its own `--unit-autonomy` overrides, as `--bot-ai` does. That is the A/B for every claim of §6: the same build, tactics on and off. |
| Commander | Sets nothing: its units take the team default, and it never changes a unit's setting, so a person sharing the army with it keeps the modes they chose. |

---

## 6. Phases

Estimates in solo-dev days, as HTN_BOTS.md §6. Every phase is under `--bot-ai htn`. The team
default (`UnitAutonomy`) is off until T8, so each phase leaves a round played as today unless a
strategist or a scenario turns micro-autonomy on.

### T0 — Sensing and the switch (1.5 days)

- `Projectile.Origin`, `ProjectileSegment.Origin`; `FireMemory`; `NearMiss` and its call in
  `ResolveSegment`; hits through `Damage`; `Doctrine` on `UnitDefinition` and the eight `.tres`;
  the three agent events and the harness's vocabulary for them.
- Micro-autonomy's server half (§5.9): `Unit.Autonomy`, the fact, the two requests, the
  strategist-only state message, `UnitAutonomy` and `--unit-autonomy`, the scenario spawn field
  and script command, the agent commands.
- Tests: `NearMissTests` (geometry, broad phase, victim excluded, fused rounds, determinism, 0 B);
  `FireMemoryTests` (window, kinds, seeded and bounded error, shooter id); `LaunchOptionsTests`;
  request validation (wrong team, not a strategist); the state message's codec; scenario and agent
  command parsing.
- **Done when:** the debug HUD counts units fired upon and near misses in a `--listen` round;
  `unit ms` at 64 units moves by less than run-to-run noise; a scratch program puts the near-miss
  pass under 20 µs a tick with 64 units and 30 rounds in flight.
- **Result — built.**
  - `dotnet test`: 1772 passed, 0 failed (1704 before; 68 new: 17 in `NearMissTests`, 13 in
    `FireMemoryTests`, 9 in `MicroAutonomyTests`, 9 in `LaunchOptionsTests`, 9 in
    `TrainedUnitResourceTests`, 7 in `ScenarioTests`, 3 in `AgentEventTests`, 1 in
    `ProjectileSimTests`). `dotnet build Gdpyr.csproj`: 0 warnings, 0 errors; the playtest harness
    and the agent client build.
  - **Cost.** A scratch program, not committed, doing what `NoteRound` does without the engine — 30
    chords a tick, each against 64 units spread over 120 m × 60 m, recording what each near one
    perceives — for 3,600 ticks after 1,000 of warm-up, `TieredCompilation` off: 7.86, 8.06 and
    10.64 µs a tick over three runs, 9.02 notes a tick, **0 B**; Intel Xeon @ 2.80 GHz, .NET SDK
    8.0.132. Without the per-chord box (`NearMiss.Chord`), building every unit's capsule first,
    it was 35.0–38.4 µs, with the same 9.02 notes a tick: the box is why the pass is under budget,
    and `NearMissTests` checks it never turns away a body the exact test keeps.
  - **In a round** (Godot 4.6 .NET, headless, through the harness; a temporary probe, not
    committed, printed each unit's fire memory and mode every second). A scripted ground seat
    firing a rifle down a line 50 m long, past three riflemen outside their own 45 m sensor: the one
    in the line recorded 5 hits, as many as the round's 5 `damage` records, and died; the one 1.2 m
    beside the line recorded 47 near misses by tick 300 and 139 by tick 840, its perceived origin at
    z = 47.5 to 51.2 against a muzzle at z ≈ 50 — on the line, within the ±5 m that 10 % of 50 m
    allows; the one 20 m off it recorded nothing. In the same round a scripted strategist seat's
    `autonomy_default on` left the three existing units as they were and changed the default; its
    `autonomy` on one unit and then another changed each, and each unit's `MicroAutonomy` fact
    followed within the tick; a spawn's `"autonomy": "on"` started that unit on. A second round,
    a rifleman 15 m from a crouched player, recorded `grenade_thrown` with `tier` 0 and `hidden` 0.
  - **Scenarios**: `./scripts/playtest.sh Tests/Scenarios/*.json` on this build, headless, default
    `htn` and micro-autonomy off: 23 of 23 passed, 0 failed, in 36 min 11 s (recorded after the
    T0 commit, on that commit's build).
  - **Differences from the plan above.** Hits are recorded where the round is resolved
    (`NoteRound` naming its victim), not inside `Damage`, so a blast and a swing are recorded by the
    same call; §4.1 says so. `unit_task` and `squad_drill` are not added yet: nothing emits them
    before T1 and T6, and a kind nothing emits is one a scenario can name and never see.
  - **Not checked.** The debug HUD's `fire` row on a display — its text is `DescribeFire`, which
    the probe printed; `unit ms` at 64 units in the engine; `ServerAutonomyState` reaching a
    networked strategist client, and the request validation (`ApplyAutonomy`, engine-side as
    `ApplyOrder` is, so not in `dotnet test`) refusing a ground peer or another side's unit — the
    round above exercised only a strategist seat's accepted commands. All four are for T1's
    display check, which has the selection card to look at.

### T1 — React to fire, the shooter, the in-game switch (1.5 days)

- §5.1; §4.1's shooter preference; the selection card's toggle, the `H` key and the HUD default
  (§5.9); the commander's Refill move marked falling back.
- Tests (`UnitHtnTests`): each row of §5.1; pre-emption of a running Obey and Advance; the Move cap
  and its reset on a hit; no React in a squad falling back; every new operator ending when its
  premise goes and when micro-autonomy goes off (the existing operator theory, extended); a switch
  on pre-empting Obey; 0 B over 64 units × 7,200 ticks.
- Scenario `tactics_react_to_fire`: riflemen ordered to move past a scripted ground seat firing
  from 50 m, beyond their 45 m sensor. Claims: `unit_task[goal=TakeCoverFromFire]` ≥ 1 every
  seed; every unit that lives arrives (the cap); units lost below the same scenario with
  `--unit-autonomy off`, over the seeds.
- **Done when:** a display check shows the card's toggle, `Mixed` on a mixed selection, and a
  second strategist's client showing the first's change.
- **Result — built.**
  - `dotnet test`: 1823 passed, 0 failed (1772 before; 51 new: 34 in `ReactToFireTests`, 11 in
    `CoverTests`, 4 in `MicroAutonomyTests`, 1 in `FireMemoryTests`, 1 in `AgentEventTests`).
    `UnitHtnTests`' 0 B population (64 units × 7,200 ticks) has two more situations, a defender
    fired upon running to cover and in it; `CommanderHtnTests` checks both Refill moves are
    falling back. `dotnet build Gdpyr.csproj`: 0 warnings, 0 errors; the harness builds.
  - **The tests test something.** One piece removed at a time, each made tests fail, and each
    restored passed: the micro-autonomy executing condition (3 failed), the cap (2), the
    not-falling-back condition (3), the doctrine condition (1), a hit lifting the cap (1), the
    Refill moves marked falling back (1).
  - **Scenario** `tactics_react_to_fire` (headless, through the harness). Its claims: 0 pilot
    fallbacks, and on every seed at least one `unit_task` with goal 18 (take cover from fire) and
    node 0 captured by the strategist side. Its first runs found a defect the unit tests had not:
    riflemen between the wall and the shooter walked round the wall's end, down the line of fire,
    to the spot behind it, and died. `CoverQuery.StaySameSide` is the fix, and the scenario's wall
    stands at z = −8. On the T1 commit's build, seeds 1–3:

    | | micro-autonomy on | off (`"unit_autonomy": "off"`, otherwise the same file) |
    |---|---|---|
    | Claims | 3 of 3 held | `unit_task` 0 of 3 seeds — the claim fails, as it should; the other two held |
    | `unit_task`: take cover from fire · return fire | 3 · 1, 2 · 1, 2 · 0 | none |
    | Hits on the riflemen (20 damage each) | 6, 7, 6 | 6, 6, 6 |
    | Riflemen killed | 0 | 0 |
    | Node 0 captured, ticks after the round starts | 1,213 every seed | 1,234 every seed |

    The riflemen take cover from fire they cannot see and still arrive (21 ticks sooner than with
    it off): T1's claims. They are not hit less for it here (19 hits on, 18 off), and nobody dies in
    either, so the plan's third claim, units lost below the off run, is not in the file: this
    geometry, one 4 m × 1.1 m sandbag wall on the way, does not show a benefit. Whether reacting
    to fire saves units is for T8 to measure, over rounds with more exposure than this one.
  - **Suite**: `./scripts/playtest.sh Tests/Scenarios/*.json` on the same build, default `htn` and
    micro-autonomy off except where a scenario sets it: 24 of 24 passed, 0 failed, in 38 min 35 s.
    Among them, the two commander scenarios, `htn_squad_retreats` and
    `htn_strategist_defends_node`, after the Refill moves were marked falling back.
  - **Display check** (Godot 4.6 .NET under Xvfb, `opengl3`, 1600 × 900: a host with
    `--listen --no-bots` and a client on it; a temporary probe, not committed, pressed keys, placed
    units, took screenshots and printed every unit's mode). Four riflemen and a builder selected:
    the card read "5 selected · tactics direct" and "Tactics: Direct (H)". After `H`: "5 selected ·
    tactics auto", the HUD line "tactics auto for 4", and on the host the four riflemen on, the
    builder off. A fifth rifleman placed and all selected: "6 selected · tactics mixed (4 of 5
    auto)", "Tactics: Mixed (H)". The HUD button read "new units: Direct", then "new units: Auto"
    after the default was switched, with no existing unit changed. The client, on the ground force,
    asked for all six units off: the host's rejected-order count went from 0 to 1 and no unit
    changed. The client then joined the strategists: the state it received was the host's —
    default on, the four riflemen on, the builder and the fifth rifleman off — and its card read
    "6 selected · tactics mixed (4 of 5 auto)". The check also showed the key list, a line longer,
    running off the bottom of the screen (26 px a line, not 24); `KeysHeight` is 260 now, and a
    second screenshot has its last line at y ≈ 874 of 900.
  - **Differences from the plan above.** The tests are in `ReactToFireTests`, not `UnitHtnTests`.
    The cap is on every order that goes somewhere (a move, a patrol, an attack), not a move only,
    and a hit lifts it (§5.1 says why). A unit that holds a point does not leave it for fire cover.
    Fire back reaches only as far as the unit's engage range and never throws a grenade at the
    fired-from point. Build's `UnderFire` is not folded into `FiredUpon` (§4.1 says why). The drill's
    moving element, which the plan excluded, does not exist until T6.
  - **Not checked.** The debug HUD's `fire` row on a display; `unit ms` at 64 units in the engine;
    `ApplyAutonomy` refusing a strategist's request for a unit of another side — only a ground
    peer's request was refused in the round above.

### T2 — Grenades behind cover (1 day)

- §5.4.
- Tests (`GrenadeTests`): `TrySolveOver` over a 1.2 m wall and a 3 m block at 8–28 m, the high
  root's fuse margin, no arc; `GrenadePlan`'s candidates (hidden only, range, freshness) and claims.
  `UnitHtnTests`: the intent; nothing with micro-autonomy off; legacy unchanged.
- Scenario `tactics_grenade_behind_wall`: a ground seat crouched still behind a low wall
  (`scripts/crouch_still.json`) 15 m from three riflemen under an attack order. Claims:
  `grenade_thrown[hidden=1]` ≥ 1 every seed; the seat killed in ≥ half. Off, no hidden throw
  happens: today's rule needs the head to show.
- **Result — built.**
  - `dotnet test`: 1868 passed, 0 failed (1823 before; 45 new: 24 in `GrenadeTests`, 15 in
    `ThrowGrenadeTests`, 6 in `CoverTests`). `UnitHtnTests`' 0 B population has a 22nd situation,
    a defender with a hidden enemy to throw at; `AgentEventTests` pins goal 20. `dotnet build
    Gdpyr.csproj`: 0 warnings, 0 errors; the harness builds.
  - **The tests test something.** One piece removed at a time; the count of tests that failed:
    the micro-autonomy condition 2, the doctrine condition 1, the no-target condition 1, the
    falling-back condition 1, the executing conditions 6, the hidden test 1, freshness 2, claims 1,
    a box stopping the arc 8, the reach check 3. The no-target condition first failed nothing:
    the executing condition still kept the throw from running, so the test only saw "not
    throwing". Without the condition the planner plans the throw, aborts it and has nothing to
    do, every tick; the test now asks for the same goal as a unit with nothing to throw at, and
    fails without it.
  - **Cost** (a temporary test, not committed; Release, Intel Xeon @ 2.80 GHz, while a scenario
    suite ran on the same four cores): `TrySolveOver` against a board of 128 boxes, 257 and 261 µs
    a call — `TrySolve`, today's solver, is 165 µs of it — down from 347–365 µs before the flight
    gathered its boxes once (one `FirstHit` over the whole board is about 2 µs, and a flight is
    about 90 chords). `Choose` over seven candidates, 3.5–4.7 µs. A unit solves on its scan only
    with a grenade ready (6 s after its last), a hidden candidate and no failed solve in the last
    second, and once more at the throw.
  - **Scenario** `tactics_grenade_behind_wall`. As planned it showed nothing about T2, and its
    runs found four things. (1) Riflemen placed in sight of the seat saw it standing before its
    first crouch, and today's rule threw at it in the open (`hidden` 0) and killed it; they now
    walk in from 66 m on a move order, after it is down. (2) The plan's 1 m chest, above a crouched
    player's recorded position, is over a 1.1 m wall: no candidate was ever hidden;
    `HiddenAimMeters` is 0.5 m. (3) A crouched player behind the wall is hidden from the riflemen
    and from the side's fog alike, so the side needs eyes on the flank: a dropship, which sees and
    never shoots. At 25 m it lost the seat as the riflemen closed in — the fog asks only the 3
    nearest sensors per contact (`SimConfig.FogLineOfSightCandidates`), and three riflemen nearer
    than it, all blind, crowded it out — so it stands 12 m off. (4) A second rifleman threw at the
    dead seat's last position after the first claim ran out; dead contacts are no longer candidates.
    Claims: 0 pilot fallbacks; `grenade_thrown[hidden=1]` ≥ 1 and `[hidden=0]` none, every seed; the
    seat killed in at least half. Seeds 1–3, on the build before the flight gathered its boxes once
    (which gives the same answers, `CoverTests`) and before the one-second back-off:

    | | micro-autonomy on | off (`"unit_autonomy": "off"`, otherwise the same file) |
    |---|---|---|
    | Claims | 4 of 4 held | the hidden throw and the kill fail on every seed, as they should |
    | `grenade_thrown`, hidden | 1 a seed, by the same rifleman on its walk, about 28 m from the seat | none |
    | Damage to the seat | 108.2, 110.5, 111.2: killed when the fuse ran out, 3 s after the throw | none; alive at the end |

    The side spots the seat 62 ticks into the round in every run, on and off. Off, the riflemen
    walk up and stand 16 m from a player they cannot see, which is today's behaviour. On the T2
    commit's own build, both runs gave the same results to the tick: the throw at tick 572, 568
    and 570, the same damage, the kill 180 ticks later; off, no throw and the seat alive.
  - **Suite**: `./scripts/playtest.sh Tests/Scenarios/*.json` on the T2 commit's build, default
    `htn` and micro-autonomy off except where a scenario sets it: 25 of 25 passed, 0 failed, in
    40 min 25 s. A first run reported `armour_small_arms` failed with a damage record naming a
    seat the scenario does not have: a suite started before it, which had been meant to be
    stopped, was still running on the same ports. With every stray server stopped, it passed.
  - **Found in T1, fixed here.** A unit firing back at a place (`FireAtPlace`) has no target of its
    own, but `Planned` passed the engine's "has a target" to `Track`, which banded it as one: under
    a defend order, an attack or none, the reaction's executing condition (nothing of its own to
    fight from cover) ended it the tick after it began firing, and the next tick, with no fire at a
    place, began it again. `Planned` now passes `hasTarget && !firingAtPlace`. Found by reading;
    no scenario shows it, because a unit fires at a place only when the shooter is within its
    40 m engage range and out of its sight, and every round from a shooter it can see within its
    45 m sensor makes that shooter its target. T1's scenario is a move order, which reacts either
    way.
  - **Differences from the plan above.** No high arc, and the arc is judged by where it comes to
    rest, not by clearing the wall (§5.4 says why). A doctrine is required, as for every tactic.
    Claims are per side, not on a squad's `DrillBoard`; squad-mates' fired-from points and the
    assault's first throws wait for T6. The hidden test aims 0.5 m above the recorded position,
    not at the chest. Contacts that have died since they were seen are not candidates. The tests
    are in `ThrowGrenadeTests`, not `UnitHtnTests`. Legacy is untouched: it has no plan, and
    `ServerThrow`'s path is the same code, with its launch (the throwing error, the spawn, the event)
    moved into `LaunchGrenade` for both paths to share.
  - **Not checked.** A grenade's cost per tick in the engine with many units; a display.

### T3 — Posture and the unit snapshot v2 (1.5 days)

- §5.8: the v2 record, the codec, the client's application of it, the unit's render, the demo
  version; crouching in `ReturnFire`, a gunner's reload and `Duck`. NETCODE.md §6.1 and §7 updated
  with the layout and the budget.
- Tests: `UnitSnapshotCodecTests` (every field round-trips, posture's reserved values decode to
  standing, decoding stays total); `DemoCodecTests` (a version-2 header is refused with its
  reason); `CoverTests` (crouched protect height); the crouched hitbox.
- **Done when:** `sandbag_cover` and the existing suite pass unchanged, a demo recorded on this
  build plays back with crouched units drawn crouched, and a display check shows a crouched unit
  behind a low wall hidden from a standing player.
- **Result — built.**
  - `dotnet test`: 1901 passed, 0 failed (1868 before; 33 new: 10 in `UnitBodyTests`, 9 in
    `UnitSnapshotCodecTests`, 8 in `TrainedUnitResourceTests`, 3 in `ReactToFireTests`, 2 in
    `UnitHtnTests`, 1 in `DemoCodecTests`). `dotnet build Gdpyr.csproj`: 0 warnings, 0 errors; the
    harness builds.
  - **The tests test something.** One piece changed at a time, the count of tests that failed:
    `ReturnFire`'s crouch removed 2; crouching with a target of its own 1; `FireBack`'s range
    check removed 1; the reload crouch without micro-autonomy 1, or out of its spot 1; reserved
    postures read raw 3; the posture left off the wire 1; a body that cannot crouch crouching 1;
    the protect height ignoring posture 2; the crouched height ignoring posture 8.
  - **Display check and demo** (Godot 4.6 .NET under Xvfb, `opengl3`, 1600 × 900; a temporary
    probe, not committed). A `--listen` host recording a demo put a sandbag wall at (−60, 48.6), a
    rifleman behind it under a defend order with micro-autonomy on, and its own player standing
    56 m in front of it, beyond the rifleman's 45 m sensor; from 7.5 s to 16 s it wrote a near miss
    from the player's position into the rifleman's fire memory every half second. The rifleman
    went to `ReturnFire` and crouched — top of the capsule 1.50 m against the wall's 1.6 m, eye
    1.35 m (the ground is at 0.5) — and stood again when the window closed, 3 s after the last
    round. Through an 8° camera at the standing player's eye: standing, its head showed over the
    wall; crouched, nothing did. The demo, played back, showed the rifleman crouched from 8.6 s to
    18.2 s and standing before and after, its client copy's posture from the records, drawn as the
    short capsule. The playback draws no sandbag wall: demos record no structures (`DemoRecordKind`
    has no kind for one), which predates T3.
  - **Found by the display check.** `FireBack` asked for fire at any fired-from point, and the
    engine refused one beyond the engage range: a unit answering a shooter at 56 m stood up to fire
    and did not, so it never crouched. The planner now applies the same range, and the T1 tests'
    shooter moved from 50 m to 35 m, inside it.
  - **Suite**: `./scripts/playtest.sh Tests/Scenarios/*.json` on the T3 commit's build, default
    `htn` and micro-autonomy off except where a scenario sets it: 25 of 25 passed, 0 failed, in
    40 min 30 s; `sandbag_cover` among them, unchanged. `tactics_react_to_fire`, whose riflemen now
    crouch in `ReturnFire`, passed with take cover from fire · return fire 3 · 1, 2 · 1, 3 · 1 a
    seed (T1's: 3 · 1, 2 · 1, 2 · 0), hits on them 6, 7, 7 (6, 7, 6), none killed, and node 0
    captured at 1,213 ticks, as before.
  - **Differences from the plan above.** Crouched 1.0 m with the eye at 0.85 m, not 1.1 m and
    0.95 m (§5.8 says why). A gunner's reload is any unit's reload in its spot under
    micro-autonomy; `Duck` waits for T4, which adds it. The display check's player was the host's
    own; a second machine's client was not used, the demo playback being the client path.
  - **Not checked.** A remote client's view of a crouched unit, beyond the demo playback that
    decodes the same records; `unit ms` at 64 units.

### T4 — Sniper (2 days)

- §5.3; `CoverBoard.Concealed`, `TryFindPeek`; `UnitIntent.HoldFire`.
- Tests: `CoverTests` for both peek shapes, concealment against several threats, determinism, 0 B;
  `UnitHtnTests` for every row, the peek clock, displacement and the recent-spot rule.
- Scenario `tactics_sniper_overwatch`: a sniper (catalog id 5) defending near a block and a low
  wall; ground bots walking at it from 90 m (roster, with H6's idle seat in the far corner).
  Claims: `unit_task[goal=FireFromPeek]` ≥ 2; `events.kill.count` ≥ 1 (the sniper is the only
  strategist unit; a unit's kill reports weapon 0, `CombatManager.WeaponOnStream`, so the filter
  cannot name the rifle); the sniper alive at the end in ≥ half the seeds; each against off.

### T5 — Gunner suppression; bots perceive it (1.5 days)

- §5.2 without the drill, over §4.2's list; §5.7.
- Tests: `UnitHtnTests` for `SuppressPoint` (bursts; live, ghost and fired-from entries; one the
  side has forgotten is not fired at; the reserve; reloading in cover; nothing without an entry);
  `GroundHtnTests` for `Suppressed` (crouch, the cap, pre-emption); `BotBrainTests` unchanged.
- **Done when:** in a bot round a gunner firing at a ground bot behind a wall keeps it crouched
  (the AI debugger shows `Suppressed` on the bot), and the bot rises after 3 s of continuous fire.

### T6 — Take the control point: the drill, and a Move to a node (3 days)

- §5.0, §5.5, §5.6. The drill in the AI debugger: a domain kind on the spectator channel
  (additive, AI_DEBUG.md §5), and world lines.
- Tests (`SquadDrillTests`): roles for each mix (gunner + 3, gunner + 6, 4 riflemen, 2, a sniper
  among them, a unit with micro-autonomy off among them); every domain row; bound alternation;
  base first; the 6 s, 8 s and 60 s give-ups; holding the point; reset on a new order;
  determinism; 0 B over 8 squads × 7,200 ticks. `UnitHtnTests`: a Move on a node is an objective
  with micro-autonomy on and is not with it off; a Move elsewhere is not; a Move objective never
  runs engage focus, support or advance.
- Scenario `tactics_move_to_node`: a scripted strategist seat moves a gunner and six riflemen to
  the middle node, held by ground bots behind its walls. Claims: the strategist captures the node
  in ≥ half the seeds; `squad_drill[phase=bound]` ≥ 1; units lost below off's over the seeds.
  Thresholds from drafts of more seeds than the file holds, as H6 set its.

### T7 — The commander uses them (1 day)

- Trains gunners at its pillboxes and snipers at its towers (`StrategistBrain`, numbers on
  `StrategistTraits`); at most one gunner per assault squad, attached as "escort armour" attaches
  tanks (HTN_BOTS.md §5.3); snipers join the garrison or a reinforcing squad, never the scout.
  The drill is the units', so this is composition only.
- Tests in `CommanderHtnTests` and `StrategistBrainTests`.

### T8 — Measure, switch on, document (1–2 days, then ongoing)

- Bot-only rounds as COVER.md §9 (five bots and the idle seat against one commander, 6 min, nine
  seeds), `--unit-autonomy on` against `off`: units lost by tier; ground players killed; node-time
  held; time from an order on a node to its capture; sniper lifetime; grenades thrown, hidden and
  not.
- The `tactics_*` scenarios pass on and fail off on the claims that separate them; the existing
  suite passes under both.
- `UnitAutonomy` defaults to on. The strategist observation gains the mode (`gdpyr-agent-obs-3`).
  Update HTN_BOTS.md §5.2 (the Move rule), COVER.md §6 and §10 (peeks), NETCODE.md (the requests,
  the state message), AGENT_API.md (the commands, the three events, obs-3), README (the toggle,
  the key, the behaviour).

Total: about 13–14 days.

---

## 7. Verification

| Claim | Proof |
|---|---|
| A situation produces a given plan | Fact-vector → task-chain tests for every new row (`UnitHtnTests`, `SquadDrillTests`, `GroundHtnTests`) |
| Every new long-running operator ends when its premise does, and when micro-autonomy goes off | The operator/premise theories, extended with the switch; the P3 case on the new branches |
| Off is today | With micro-autonomy off on every unit, the H3–H6 and cover tests and the existing scenario suite pass unchanged |
| Nothing allocates on the tick | Zero-byte runs over the unit domain, the drill, `NearMiss`, `FireMemory`, the peek and arc searches, each with a `DefaultFactory` run that must allocate |
| Deterministic | Two-run trace equality with micro-autonomy on, near-miss pass and perception error included |
| Legacy is intact | `UnitBrainTests`, `BotBrainTests`, `StrategistBrainTests` unchanged |
| The tactics are what helps | Each `tactics_*` scenario passes on and fails off; T8's rounds |
| It costs nothing noticeable | `unit ms` at 64 units, on against off; a scratch-program figure per phase; the v2 record's bytes in the debug HUD's `out` row |
| The guards can fail | Per phase, as H2–H6 did: remove an executing condition, a cap, a claim, the base-first rule, the switch's executing condition; each fails a named test, then is restored |

---

## 8. Decisions

| # | Question | Decided |
|---|---|---|
| T-D1 | Does a Move to a control point become a goal the units pursue tactically? | Yes: taking the point is the goal; moving into cover, suppressing fire, grenades and team movements are its subgoals (§5.0). |
| T-D2 | Do near misses count, or only hits? | Near misses count (§4.1). |
| T-D3 | How well does a unit know where a shot came from? | Direction exactly, range to 10 %, for now (`FiredFromErrorFraction`, §4.1). |
| T-D4 | How does posture get onto the wire? | A clean, versioned schema rather than a spare bit: unit snapshot v2 and demo format 3; old demos are not supported (§5.8). |
| T-D5 | Where may a gunner suppress? | Anywhere an enemy is seen now or was last seen, and anywhere fire came from, for as long as the side remembers it (§4.2, §5.2). |
| T-D6 | Ground bots perceive fire; people get nothing. | Yes (§5.7). |
| T-D7 | How are the tactics switched off? | Per unit, by the strategist in game, as a fact the unit's HTN observes (§5.9). It is also the A/B switch, replacing the first cut's `--unit-tactics`. |

Choices this revision makes to carry those out, open to objection:

| # | Choice | Why |
|---|---|---|
| F1 | No `Rush` order. | Micro-autonomy off is "just go" for a person; the commander's retreats and refills are marked falling back, and units falling back do not react (§5.6). One mechanism instead of two. |
| F2 | Off means today's `htn` unit behaviour, not legacy's FSM. | It is what units do now, so off is a baseline that has been measured (H6, COVER.md §9), and switching does not change how cover and focus fire work under every other order. A stricter third mode is a value of the same enum if it is wanted. |
| F3 | The setting goes to strategists only, not into the unit snapshot. | The unit snapshot reaches every ground client unfogged; a setting is an order, and orders are not on the wire (NETCODE.md §6.3). |
| F4 | One default for new units per team; the last strategist to set it wins. | Two strategists share one army (HTN_BOTS.md §1); a per-strategist default would make a unit's mode depend on which barracks queue a strategist clicked. |
| F5 | The observation gains the mode only in T8, with `gdpyr-agent-obs-3`. | A layout change makes clients refuse obs-2 policies (AGENT_API.md §4); one bump, when the default flips, rather than one per phase. |

---

## 9. Risks

| Risk | Signal | Mitigation |
|---|---|---|
| Pinned for ever: react, suppression and drills keep a squad in cover | Time from an order on a node to its capture grows; `squad_drill` phases repeat | Caps on every phase and on a Move's react (§5.1, §5.5); give-ups with a retry delay (P4) |
| Orders feel ignored | Strategists report units "stopping" on a move | The toggle (§5.9); React's cap on a plain Move; the drill visible in the AI debugger |
| Plan thrash at band edges | The debugger's history alternates goals each scan | Hysteresis on `FiredUpon` (its window), `Range` (2 m) and `Peek`; the drill on a 10-tick cadence |
| Too strong | The ground force loses bot-only rounds it used to win | Every number on `UnitPlanTraits` / `DrillTraits`; the team default |
| Privilege creep | Units act on what a person in their place would not know | Fired-from points with error, the unit's and squad's only; nothing into `ContactMemory`; no new rays; suppression bounded by the side's memory |
| Cost | `unit ms` grows | A broad phase on the near-miss pass; box arithmetic only; one drill decision a tick |
| Wire | The v2 record and the new messages | One byte a unit; codec tests; demo format 3 refuses older demos by its header check |
| Map dependence | `Test.tscn`'s 18 low walls and 8 blocks; peeks and bounds find little elsewhere | Every new branch falls through to today's behaviour when it finds no spot; the dig-in (COVER.md §7) adds walls |
