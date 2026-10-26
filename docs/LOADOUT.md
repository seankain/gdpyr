# Ground-force loadout: the locker panel, body armour and weight — implementation plan

Status: **L0 built and measured (§10); L1–L8 planned.** Planned from the code at `dd579dd`. Phases
L0–L8 (§10); decisions L-D1 to L-D15 (§3) are the defaults the plan is written against — flag any
you disagree with before L1 starts. L0's measurement raised the default kit to five magazines, and
so the pouches to eight (L-D1, L-D3, L-D4, L-D13; §10 L0's result).

The three strands asked for are **L3 (body armour)**, **L4 (weight)** and **L6 (the locker
panel)**. L1, L2 and L5 are what they stand on: a loadout that can hold more than a large weapon,
finite primary ammunition, a player record with room for them, one validated path by which a kit
is taken, and resupply at the locker. L7 and L8 keep the bots, the agent API, the harness and the
docs true.

Companion documents: [`NETCODE.md`](NETCODE.md) §7 (the player record, `WeaponFlags`), §10.6 (the
weapon locker this replaces), §10.7 (unit resupply, whose lockout this copies), §10.9 (hand
grenades); [`IMPLEMENTATION_PLAN.md`](IMPLEMENTATION_PLAN.md) §M2 (the loadout), §M5 (carry speed);
[`AGENT_API.md`](AGENT_API.md) §6.1, §7, §8, §9.1; [`HTN_BOTS.md`](HTN_BOTS.md) §5.1 (the ground
domain, the locker runner); [`DEMOS.md`](DEMOS.md) §5.

---

## 1. What is asked

1. **Resupply at the locker.** A ground-force player within one metre of a weapon locker is
   resupplied with ammunition and armour automatically.
2. **The use key opens a loadout panel.** `E` (the `use` action) at a locker opens it.
3. **The panel chooses** the primary weapon, slots that each hold extra primary ammunition or a
   grenade, and light, medium or heavy armour.
4. **Weight.** Every component weighs something, and the total lowers movement speed: heavier
   armour and weapons, slower player.
5. **Everything is optional**, as in the Counter-Strike buy menu: no primary, no magazines, no
   grenades, no armour — the fastest player, and the easiest to kill.
6. **Steps for the ground force's armour, weight and the locker UI** — the phases of §10.

## 2. What the code does today

| Concern | Today | Where |
|---|---|---|
| Loadout | Three slots: hammer, pistol, one large weapon (DMR, rifle, launcher). `Sanitize` forces the first two and maps anything else in the third to the rifle. Nothing else is chosen. | `Scripts/Sim/WeaponSim.cs` (`LoadoutSelection`), `Scripts/Core/WeaponCatalog.cs` (`Sanitize`, `LargeWeapons`) |
| Choosing it | `1`/`2`/`3` while dead or between rounds; keyboard only; next spawn only. Request `ClientSelectLoadout(byte)`, sanitized on the server into `PendingLoadout`. | `Scripts/Ui/LoadoutMenu.cs`, `Scripts/Match/CombatManager.cs:2043–2078` |
| The locker | A marker node, no collider, no state. A **tap** of use within 2.5 m (3-D distance to its origin) swaps the large weapon for the next — rifle → launcher → DMR — with a full magazine, for this life only. | `Scripts/Fps/WeaponLocker.cs`; `EmplacementSim.Resolve` → `UseAction.SwapWeapon` (`Scripts/Sim/Emplacements.cs:215`); `EmplacementManager.SwapWeapon` (`Scripts/Fps/EmplacementManager.cs:274`); `LockerSim.Next`; `SimConfig.LockerReachMeters` |
| Ammunition | **No reserve.** `WeaponSim.CanReload` checks only `Ammo < MagazineSize`: every weapon reloads forever, players' and units' alike. | `Scripts/Sim/WeaponSim.cs:236` |
| Grenades | Three a life (`GrenadeSim.Carried`), refilled only by a respawn; the snapshot can count three (`SimConfig.MaxGrenades`, two bits). | `Scripts/Fps/PlayerCombat.cs:182`, `Scripts/Sim/Grenades.cs` |
| Player damage | 100 health. `CombatManager.Damage` subtracts the amount whole: no mitigation of any kind for a player. Called from the swing (`:581`), a round's contact (`:668`) and a blast (`:918`). | `Scripts/Match/CombatManager.cs:1155`, `PlayerCombat.ApplyDamage` (`:215`) |
| Speed | Each movement state's authored speed (idle and walk 5 m/s, sprint 8, crouch 2) times `fps_controller.MoveSpeedScale`, set before every simulated tick, server and owning client alike, from `EmplacementManager.MoveScaleOf` (carrying a gun 0.55, a can 0.8, mounted 0). A slide needs more than 6 m/s. | `Scripts/Net/PlayerManager.cs:871`, `Scripts/Fps/fps_controller.cs:76`, `Scripts/Fps/PlayerSprintingState.cs`, `Scenes/fps_controller.tscn` |
| Player record | 29 B. The weapon byte is full: slot 2 · reloading 1 · team 1 · large weapon 2 (index + 1; 0 "not said") · grenades 2. | `Scripts/Sim/PlayerSnapshot.cs:15`, `Scripts/Sim/SnapshotCodec.cs`, `Scripts/Sim/WeaponFlags.cs` |
| HUD | `HP · grenades` and `weapon / magazine size`; the use-key prompt names the locker's swap. | `Scripts/Ui/CombatHud.cs:148–152`, `EmplacementManager.PromptFor` |
| Bots | Loadout by roster slot (rifle, DMR, launcher, …). The HTN locker runner taps use at the locker until it holds a launcher. | `Scripts/Bots/BotDirector.cs:749`, `Scripts/Bots/BotPilot.cs:663`, `Scripts/Sim/Htn/GroundDomain.cs` (`UseLocker`) |
| Agent API | Ground observation has `move_speed_scale`; no grenade count, reserve or armour. `locker_launcher_vs_tank.json` taps use through `scripts/locker_then_fire.json`. | `Scripts/Sim/Agent/AgentObservation.cs`, `Scripts/Agent/AgentGroundView.cs:96` |
| Demos | Format version 3; journal playback re-simulates movement and does not set `MoveSpeedScale`. | `Scripts/Sim/Demo/DemoFormat.cs:102`, `Scripts/Net/PlayerManager.cs:677` |

## 3. Decisions

| | Decision | Why |
|---|---|---|
| L-D1 | **A loadout is a primary, eight pouches and a vest.** Primary: none, DMR, rifle or launcher. Each pouch holds one reload of the primary or one hand grenade; at most three grenades. Vest: none, light, medium, heavy. The hammer and the pistol are always carried. | "Slots for extra primary ammunition or grenades" as one shared budget makes the trade the request describes. Three grenades is the snapshot's two bits and today's count. A stripped player still has a pistol, as Counter-Strike keeps the knife and pistol. Eight rather than the six first planned, so the default kit L0 measured (L-D3) still fills them with today's three grenades, as three and three filled six. |
| L-D2 | **Primary ammunition is finite.** The large weapon's spare rounds live on its `WeaponState` (`Reserve`). A pouch holds one magazine — rifle 30, DMR 10 — or two launcher grenades. A reload moves `min(missing, Reserve)` rounds; with none spare the weapon does not reload and dry-fires. The pistol, every unit weapon and every mounted gun keep unlimited reloads (`Reserve = -1`). | Spare magazines mean nothing while every reload is free. The sentinel keeps units and the heavy gun byte-for-byte as they are. |
| L-D3 | **The default kit** — a person who never opens the panel, every bot until L7, every agent seat — is **rifle, 5 magazines, 3 grenades, no armour**. Planned as 3 magazines, with the rule that if L0 found the 95th percentile of rifle rounds per life above 120 (30 + 90), the magazines are raised and §4.1's reference weight with them. L0 found 170 (§10): five is the fewest that cover it (30 + 150 = 180). | No armour keeps today's lethality, and so every scenario's measured times to kill; three grenades are today's three; L0 made "the default rarely runs dry" a measurement rather than a hope — at five, 4.7 % of HTN rifle lives would have run dry, against 11.0 % at three. |
| L-D4 | **Speed scale = clamp(1 + 0.02 × (W_ref − W), 0.70, 1.12)**, `W` the loadout's weight in kg and `W_ref` the default kit's, 9.3 kg (8.3 before L0 added two magazines). Multiplies the carry scale; applies to every movement state's speed; not to jump height. | The default kit moves exactly as today, so bot strafing, slide thresholds and every scenario's timing stand. Stripped: walk 5.6, sprint 9.0 m/s. Heaviest: walk 3.5, sprint 5.6. 2 % a kilogram puts the common kits where Counter-Strike's rifles sit against its knife (≈ 0.86–0.90). |
| L-D5 | **Weight is the kit as taken, not as spent.** It changes at a spawn and at a locker and nowhere else; firing a magazine away does not make anyone faster. | Speed then changes only on server events the record already carries, so the owning client predicts it exactly (§7.1), and a player reads their speed off the panel. |
| L-D6 | **Body armour is points and a share.** A vest absorbs its share of each bullet or swing — light 35 %, medium 50 %, heavy 65 % — and half that share of a blast, up to its points: 35, 60, 100. What it absorbs comes off its points; at zero it does nothing until resupplied. Weights 3, 6, 10 kg. Named `BodyArmour` in code. | The Counter-Strike model, which a player already understands, and one function to test (§4.3). The name keeps it apart from `Armour` (`Scripts/Sim/Armour.cs`), the `BulletDamageScale` rule for tanks and structures. |
| L-D7 | **Resupply within 1 m.** A living ground-force player whose feet are within 1.0 m, flat, of a locker's **footprint** (the 1.2 × 0.5 m cabinet, rotated with its node) is topped up to what their kit says: one pouch's rounds every 0.5 s, one grenade every 1 s, 30 armour points a second — and nothing while anything has hurt them in the last 3 s (`Resupply.DamageLockoutTicks`). Health is not resupplied. | The request's metre, measured from the cabinet rather than its origin: the cabinet has no collider and a player can stand in it, and from the origin a metre reaches 0.75 m past the front face but only 0.4 m past either end. Paced and locked out for §10.7's reason: resupply, not invulnerability at the door. |
| L-D8 | **The use key at a locker opens the panel.** A tap resolves to `UseAction.OpenLocker`, which replaces `SwapWeapon`; the cycle (`LockerSim.Next`, `WeaponCatalog.LockerSwap`) is retired. The locker's reach becomes the same 1.0 m from the footprint (`SimConfig.LockerReachMeters`); guns and cans keep 2.5 m. | One locker, one distance: everything the panel takes is then also resupplied, and the request reads the two as one place. |
| L-D9 | **A kit is a request, validated in one place.** The panel is client UI; its choice travels as `ClientLockerLoadout` and the server accepts it only for a living ground-force player, not mounted and carrying nothing, within the locker's reach plus 0.5 m of latency slack, with a kit `Sanitize` leaves unchanged. Bots and agent seats send the same request to the same validator, not keys. | NETCODE §10.6's argument against a menu was that its keys would have to be recorded input; moving the choice out of the input frame into a request — the pattern `ClientSelectLoadout` already is — answers it, and keeps a person, a bot and a policy on one rule. |
| L-D10 | **Taking a kit fills what changed.** A different primary comes with a full magazine and full pouches; a different vest is full. What did not change keeps its state and is left to resupply. | Opening the panel under fire cannot refill a magazine the 3 s lockout would have refused. |
| L-D11 | **A locker kit is also the next spawn's** (`PendingLoadout`). The death screen edits the same kit in the same panel; `1`/`2`/`3` while dead are retired. | Supersedes §10.6's "it is for this life": with one kit and one editor, a player who built a kit expects to respawn in it. Agent episodes still start alike — `reset` restores each seat's configured kit (§9). |
| L-D12 | **The panel holds the player still.** While it is open the sampler sends neutral frames (`InputFocus.Menu`, beside `TextEntry`), the mouse is released, and `E`, `Esc` or `Enter` closes it. It closes itself on death, side change, round end, leaving reach, or a refusal. | The only way its keys and pointer can mean panel things without being recorded as input. The spawn is the ground force's own ground. |
| L-D13 | **The player record goes from 29 to 32 B** (§6.1): spare rounds, armour points, and a kit byte (vest 2 bits · magazine pouches 4 · grenade pouches 2). Zero in the large-weapon bits now means **no primary**. `DemoFormat.Version` 3 → 4; an older demo is refused with its reason. | The owner derives its whole kit — and so its weight and speed — from the record, the one thing a client is never wrong about for long; no new state message. +0.7 KB/s per client at eight players. Redesigned, not squeezed, as TACTICS T-D4 did the unit record. |
| L-D14 | **Bots** carry the default kit until L7. Under either AI a bot whose primary is dry switches to its pistol. Under `htn` a dry bot with nothing in sight walks back to the locker (`Rearm`), and the locker runner asks for a launcher kit instead of tapping. Per-role kits for HTN bots are adopted only if the `htn_*` scenarios still pass. Legacy keeps the default. | Legacy is the training baseline (TRAINING.md §3); it changes as little as finite ammunition allows, and is re-measured once in L8. |
| L-D15 | **Agent API.** JSON observation fields, the `loadout` op, the `kit` event, the `spawn` op's `kit` field, the scripts' `loadout` step, and the `damage` and `kill` events' `absorbed` and `rounds_fired` are additive. The flat ground vector gains five floats at its end, so the schema goes to `gdpyr-agent-obs-4` (L8). | Every old client keeps reading JSON; the vector changes size, which is what the schema version is for (`AgentProtocol.SchemaVersion`, T8's precedent). |

## 4. The rules, engine-free

Each of these lives under `Scripts/Sim` and is tested by `dotnet test` with no engine, as every rule
the server and the owning client both run must be.

### 4.1 The loadout and its weight — `Scripts/Sim/Loadout.cs` (new), `Tests/LoadoutTests.cs`

`LoadoutSelection` (`Scripts/Sim/WeaponSim.cs`) gains three bytes: `Magazines`, `Grenades`,
`Armour` (`BodyArmourTier`). `Large` may be `Loadout.NoPrimary` (255), which `WeaponCatalog.None`
aliases: the constant lives in `Scripts/Sim` because the catalog (`Scripts/Core`) loads resources
and is not compiled into the tests. `Loadout` holds the constants and the pure functions:

- `PouchSlots = 8`; `Sanitize(kit, ReadOnlySpan<byte> largeWeapons)`: an unknown primary is the
  rifle (as today), `NoPrimary` is kept; `Magazines = 0` with no primary; `Grenades ≤
  SimConfig.MaxGrenades`; `Magazines + Grenades ≤ PouchSlots`, grenades kept first when trimming; an
  unknown vest is none. `WeaponCatalog.Sanitize` calls it with `LargeWeapons`, as `LockerSim` is
  handed its choices today.
- `WeightKg(in LoadoutSelection, ReadOnlySpan<CarryStats>)`: base kit + primary + pouches + vest.
- `SpeedScale(float weightKg)`: L-D4's line.
- `ReserveFor(in LoadoutSelection, in CarryStats)`: `Magazines × PouchRounds`.

`CarryStats` (`WeightKg`, `PouchRounds`, `PouchWeightKg`) is per weapon, exported on
`WeaponDefinition` and loaded by `WeaponCatalog` beside `WeaponStats` (`WeaponCatalog.CarryFor`), so
weights are tuned in the `.tres` files and the same numbers load on every process.

| Component | kg | Pouch holds | kg a pouch |
|---|---|---|---|
| Base kit: hammer, pistol and its magazines (fixed) | 2.0 | — | — |
| Rifle | 3.6 | 30 rounds | 0.5 |
| DMR | 4.4 | 10 rounds | 0.6 |
| Launcher | 6.0 | 2 grenades (40 mm) | 0.9 |
| Hand grenade | — | 1 | 0.4 |
| Vest: light / medium / heavy | 3 / 6 / 10 | — | — |

| Kit | kg | Scale | Walk / sprint m/s |
|---|---|---|---|
| Pistol only | 2.0 | 1.12 (cap) | 5.6 / 9.0 |
| Rifle, no pouches | 5.6 | 1.07 | 5.4 / 8.6 |
| **Default:** rifle, 5 mags, 3 grenades | **9.3** | **1.00** | **5.0 / 8.0** |
| Rifle, 8 mags, light | 12.6 | 0.93 | 4.7 / 7.5 |
| Rifle, 5 mags, 3 grenades, medium | 15.3 | 0.88 | 4.4 / 7.0 |
| DMR, 5 mags, 3 grenades, medium | 16.6 | 0.85 | 4.3 / 6.8 |
| Rifle, 5 mags, 3 grenades, heavy | 19.3 | 0.80 | 4.0 / 6.4 |
| Launcher, 8 pouches, heavy | 25.2 | 0.70 (floor) | 3.5 / 5.6 |

The last kit cannot slide: its sprint never passes the sliding state's 6 m/s. Accepted (§11).

Tests: sanitize (each rule, hostile bytes, `None` kept, trimming order); the table above to
0.005; the default kit is exactly 1.0; monotonic in every component; the cap and the floor.

### 4.2 Primary ammunition — `Scripts/Sim/WeaponSim.cs`, `Tests/WeaponSimTests.cs`

- `WeaponState.Reserve` (`short`), `-1` meaning unlimited; `WeaponState.Ready(id, stats, reserve =
  -1)`, so every existing caller is unchanged.
- `CanReload` adds `Reserve != 0`. `ReloadFinish` moves `min(MagazineSize − Ammo, Reserve)` rounds
  when the reserve is finite. The empty-magazine path is unchanged: with nothing spare it is already
  `DryFire`.
- `WeaponState.Empty`, and `WeaponSim.SelectSlot(current, input, occupied)`: a key for an empty
  slot does nothing, on both ends, because the mask comes from the kit both ends know.

Tests: a reload takes from the reserve; a partial reload keeps the remainder; no reserve, no reload,
and a dry fire; the launcher's one-round magazine consumes one a cycle; `-1` reloads forever (a
unit's weapon, unchanged); the empty slot cannot be selected; the step stays a pure function of its
arguments.

### 4.3 Body armour — `Scripts/Sim/BodyArmour.cs` (new), `Tests/BodyArmourTests.cs`

```
BodyArmourTier { None, Light, Medium, Heavy }
BodyArmourState { Tier, Points }
float BodyArmour.Absorb(ref BodyArmourState vest, float damage, bool explosive)  // returns damage to health
```

`absorbed = min(damage × share × (explosive ? 0.5 : 1), Points)`, rounded to a whole point; `Points
−= absorbed`; returns `damage − absorbed`. Explosive is the round's (`ProjectileStats.IsExplosive`),
as for `Armour`; a swing is not.

Hits to kill a still player from full health and a full vest, as `BodyArmourTests` pins them:

| Round (damage) | None | Light | Medium | Heavy |
|---|---|---|---|---|
| Rifle, heavy gunner's LMG (20) | 5 | 7 | 8 | 10 |
| Turret, pillbox, technical (45) | 3 | 3 | 4 | 5 |
| DMR, marksman, sniper (60) | 2 | 3 | 3 | 4 |
| Hand grenade blast at 3 m (80) | 2 | 2 | 2 | 2 |

Tests: the table; zero points absorbs nothing; a blast at half the share; never negative; never
more than the points; determinism.

### 4.4 Resupply at the locker — `Scripts/Sim/LockerSupply.cs` (new), `Tests/LockerSupplyTests.cs`

- `FootprintDistance(Vector3 feet, Vector3 origin, float yaw)`: flat distance from the feet to the
  rotated 1.2 × 0.5 m rectangle; 0 inside it.
- `InReach(distance) => distance <= SimConfig.LockerReachMeters` (1.0, L-D8).
- `Step(ref KitState kit, in LoadoutSelection loadout, in CarryStats primary, uint tick, uint
  lastDamagedTick)`: one pouch's rounds every `PouchIntervalTicks` (30), one grenade every
  `GrenadeIntervalTicks` (60), `ArmourPerSecond` (30) a tick's worth, none inside
  `Resupply.DamageLockoutTicks`; returns what it gave, for the debug HUD and the `kit` counters.

Tests: the footprint at both rotations of the Test map's locker (it is turned 180°); 0.99 m in,
1.01 m out; the pacing; the lockout; never past the kit; nothing for an empty vest or no primary.

### 4.5 The use key — `Scripts/Sim/Emplacements.cs`, `Tests/EmplacementTests.cs`

`UseAction.OpenLocker` replaces `SwapWeapon` in `EmplacementSim.Resolve`, in the same row: empty
handed, a tap, the locker nearer than any gun or can. `LockerSim` and `WeaponCatalog.LockerSwap` go.
Tests: the existing locker rows, renamed; a hold at a locker is still nothing; carrying a can at a
locker still puts it down.

## 5. The server

### 5.1 `PlayerCombat` (`Scripts/Fps/PlayerCombat.cs`)

- `Equip(loadout)`: the large slot is `WeaponState.Ready(id, stats, Loadout.ReserveFor(...))`, or
  `WeaponState.Empty` with no primary, in which case the life starts with the pistol up; grenades
  start at `loadout.Grenades`, not `GrenadeSim.Carried`; the vest at its tier's points.
- `ApplyKit(loadout)`: L-D10 — re-equips only what changed. Replaces `SwapLarge`'s use at the
  locker; the client keeps a `SwapLarge`/`ClearLarge` for what the record says.
- `Vest` (`BodyArmourState`); `LastDamagedTick`, set in `Damage`, for the lockout.
- `ApplyAuthoritative` reads the record's three new bytes (§6.1): reserve under the same
  acknowledgement guard as the magazine, the vest whole, the kit byte into `Loadout` (so `Loadout`
  on a client is the server's word, which the weight is computed from).

### 5.2 Damage (`Scripts/Match/CombatManager.cs`)

`Damage` gains `bool explosive` and runs `BodyArmour.Absorb` before `ApplyDamage`. The three call
sites pass it: the swing `false`, a round's contact `stats.IsExplosive`, a blast `true`. The `damage`
agent event keeps `amount` as health lost — what reward functions read — and gains `absorbed`. A hit
that the vest absorbed whole is still confirmed: the shooter did something.

### 5.3 One validator for a kit

`CombatManager.TryApplyLockerKit(PlayerCombat, LoadoutSelection, out KitRefusal)`: alive, ground
force, not mounted, not carrying (`EmplacementManager.CarryOf`), a locker within `LockerReachMeters
+ 0.5`, `Loadout.Sanitize(kit) == kit`. On success `ApplyKit`, `PendingLoadout = kit`, the `kit`
event, a log line. Three callers: the `ClientLockerLoadout` RPC, the bot pilot (§8), the agent
`loadout` op (§9). At most one accepted kit per 0.25 s per player, so a held key or a script cannot
make the server re-equip every tick.

`ClientSelectLoadout` is widened to the whole kit and stays next-spawn only, accepted at any time.

### 5.4 The resupply pass

In `ServerPostTick`, after `ServerRespawns`: for each living ground-force player in reach of a
locker (`LockerSupply.FootprintDistance` over `EmplacementManager`'s lockers, which gains their yaw),
`LockerSupply.Step`. Counters for the debug HUD: players resupplied, rounds, grenades and armour
points given this round.

### 5.5 Speed (`Scripts/Net/PlayerManager.cs:871`)

`MoveSpeedScale = carryScale × Loadout.SpeedScale(Loadout.WeightKg(combat.Loadout, ...))`, the
second factor cached on `PlayerCombat` and recomputed when the kit changes. Same line, server and
owning client.

## 6. On the wire

### 6.1 The player record, v2

| Offset | v1 (29 B) | v2 (32 B) |
|---|---|---|
| 0–27 | peer id, position, velocity, yaw, pitch, last input tick, state, buffer depth, health, ammo | unchanged |
| 28 | weapon: slot 2 · reloading 1 · team 1 · large 2 (0 = not said) · grenades 2 | unchanged bits; large **0 = no primary** |
| 29 | — | **reserve**: the primary's spare rounds, clamped to 255 (rifle at eight pouches: 240) |
| 30 | — | **armour points** |
| 31 | — | **kit**: vest 2 · magazine pouches 4 · grenade pouches 2 |

`PlayerSnapshot`, `SizeBytes`, `SnapshotCodec`, `WeaponFlags` (the zero's meaning), a new
`KitFlags`, and `PlayerCombat.ApplyAuthoritative` change together. Cost: 3 B × 8 players × 30 Hz ≈
0.7 KB/s per client against NETCODE §7's ≈ 20 KB/s.

The record goes to every client, as the ammo byte already does: a strategist's client can read a
player's vest. The units do nothing with it (§13).

### 6.2 Messages

| Message | Direction | Transfer | Rate | ~Size |
|---|---|---|---|---|
| `ClientSelectLoadout` (widened) | C→S | Reliable | per choice | ~7 B |
| `ClientLockerLoadout` (new) | C→S | Reliable | per kit taken | ~7 B |
| `ServerKitResult` (new, to the requester) | S→C | Reliable | per request | ~4 B |

`ServerKitResult` carries applied or the refusal (`dead`, `not_at_locker`, `mounted`, `carrying`,
`invalid`, `too_soon`); the panel closes on the first and shows the second.

### 6.3 Demos

`DemoFormat.Version` 3 → 4; `DemoHeader`'s check refuses 3 with its reason, no translation layer
(as T3 did). Kit requests are not journalled: the record carries their consequences. Journal
playback sets each character's `MoveSpeedScale` from its keyframe's kit byte in
`ApplyPlaybackSnapshot`, so a heavy player's re-simulated walk is not dragged back at every
keyframe; the carry scale stays unrecorded, as today.

## 7. The client

### 7.1 Prediction

- **Speed** is derived from `Loadout`, which only the record writes on a client; it changes a round
  trip after the server changed it, and the replay uses the current scale — the same correction a
  carry already costs (`fps_controller.MoveSpeedScale`'s comment).
- **Reloads** are predicted from the predicted reserve; the server's reserve is applied only once
  it acknowledges every predicted weapon tick (`LastPredictedWeaponTick`), as the magazine is.
  Resupply is server-only and arrives through that byte.
- **Grenades**: unchanged mechanism; the count's ceiling is the kit's, not three.

### 7.2 The locker panel — `Scripts/Ui/LoadoutPanel.cs` (new, replaces `LoadoutMenu.cs`)

Built in code, as `CombatHud` is, on `Ui/theme.tres`; a child of `CombatHud`.

```
┌ WEAPON LOCKER ─────────────────────────────────────────────────────────┐
│ PRIMARY   [1 none]  [2 rifle 3.6 kg]  [3 DMR 4.4 kg]  [4 launcher 6.0 kg] │
│ POUCHES   ▣▣▣▣▣◆◆◆   magazines [5 −] 5 [6 +]   grenades [7 −] 3 [8 +]     │
│ ARMOUR    [none]  [light 3 kg]  [medium 6 kg]  [heavy 10 kg]   [9 −][0 +] │
│                                                                          │
│ LOAD 15.3 kg   SPEED 88 %  ██████████████░░   walk 4.4 · sprint 7.0 m/s  │
│ VEST 60 pts, absorbs 50 %   rifle hits to kill 8 (5 without)             │
│ AMMO 30 + 150 rifle · 3 grenades                                         │
│                                                                          │
│ [Enter] take this kit   [Backspace] what I carry   [D] default   [E] close│
└──────────────────────────────────────────────────────────────────────────┘
```

- **Choices.** Primary and vest are radio rows. Pouches are two steppers over eight cells drawn
  magazines-first — only the counts mean anything; `+` is disabled at eight pouches, grenades at
  three, magazines with no primary. Mouse and keys do the same things.
- **Read-outs** come from §4's functions — weight, scale, speeds, the vest's hits-to-kill against a
  rifle — so the panel cannot disagree with the simulation.
- **Opening.** The local player's own use tap, resolved by the same `UseTracker` and
  `EmplacementSim.Resolve` the server runs, from the predicted frames in
  `CombatManager.ClientSimulateLocal`, against the locally known lockers, guns and cans — the
  computation `EmplacementManager.PromptFor` already makes. The prompt reads `[E] weapon locker`.
- **While open** (L-D12): `InputFocus.BeginMenu()`; `LocalInputSampler.Sample` treats it as it
  treats `TextEntry`; `Input.MouseMode = Visible`; `PauseMenu` ignores `Esc` while the panel is open,
  so `Esc` closes the panel and not the game.
- **Taking.** `Enter` sends `ClientLockerLoadout` through `CombatManager.RequestLockerKit` (applied
  directly on the authority, as `RequestLoadout` is) and is disabled until the answer arrives.
  `ServerKitResult` closes the panel, or puts the refusal in the footer and leaves it open.
- **Closing.** `E`, `Esc`, an applied result, death, side change, round end, leaving reach (checked
  each tick from `LockerSupply`), the role menu: `InputFocus.EndMenu()` and
  `fps_controller.EnterFirstPerson()` recaptures the mouse.

### 7.3 The death screen

`CombatHud` shows the next spawn's kit in one line — `next spawn: rifle · 5 mags · 3 grenades · no
vest · 9.3 kg · 100 %   [E] edit` — and `E` opens the same panel in next-spawn mode, whose `Enter`
sends `ClientSelectLoadout`. A dead player's look is unaffected until they press it.

### 7.4 The HUD

- Ammunition corner: `rifle  30 / 150` (magazine / reserve) for the primary; `pistol  12 / ∞`;
  `rifle  0 / 0  dry` when spent; `—` with no primary.
- Status: `HP 100   vest 60   grenades 3`, and `load 15.3 kg · 88 %` under it.
- Prompt while resupplying: `resupplying — 4/5 mags · 40/60 vest`, or `resupply waits: under fire`
  inside the lockout.
- Debug HUD (`Scripts/Ui/Debug.cs`): a `kit` row — players by vest tier, mean load, kits taken,
  rounds/grenades/points resupplied this round.

### 7.5 Presentation

A remote player's vest is drawn as a greybox chest band coloured by tier, from the record. The
locker cabinet's label gains `RESUPPLY` and a 1 m ring on the floor around the footprint, the way a
supply truck's reach is drawn.

## 8. Computer players

- **Default kit** for every bot until L7: `BotDirector.Equip` keeps choosing the primary by roster
  slot and fills the rest from L-D3.
- **Dry → pistol**, both AIs: `BotSituation.PrimaryDry` (magazine and reserve zero); `BotBrain`
  presses `Weapon2`, and `Weapon3` again once the reserve is back. `BotBrainTests`.
- **`Rearm`**, HTN only: `GroundFact.Ammo` (`Ok`, `Low` below one magazine spare, `Dry`); a goal
  under the existing top-level choice, after fighting a contact in sight and before advancing: path
  to within 0.8 m of the nearest locker's footprint (`EmplacementManager.TryNearestLocker`, which
  gains the footprint), stay until full or 6 s. `GroundHtnTests`; `HtnDomains` (the debugger's map)
  and `HtnDebugTests` follow the domain.
- **The locker runner** reaches the locker and asks for a launcher kit through §5.3's validator; it
  no longer taps (`BotSituation.Use`, `GroundTraits.UseTapIntervalTicks` and the pilot's tap go).
  `FailedLockerTrips` counts refusals and timeouts as it counts timeouts now.
- **Per-role kits** (L7, HTN only, behind `GameModeDefinition.BotKits = default | role`): rifle — 4
  mags, 2 grenades, light; DMR — 5 mags, 1 grenade, none; launcher — 3 pouches (6 rounds), 3
  grenades, medium. `role` becomes the default only under §10 L7's condition.
- The AI debugger's inspect panel shows a bot's kit, reserve and vest.

## 9. Agent API, harness and scenarios

- **Observation (JSON, additive):** `self.grenades`, `self.armour`, `self.armour_max`,
  `self.armour_tier`, `self.load_kg`, `self.load_scale`; `weapon.reserve`, `weapon.reserve_max`. Each
  lands with the phase that adds its state (L1, L3, L4), so scenarios can assert on it.
- **Flat ground vector (L8):** five floats appended — grenades / 3, reserve fraction, armour
  fraction, tier / 3, load scale / 1.12 — `gdpyr-agent-obs-4`; `move_speed_scale` stays clamped to
  1 as before. `AgentObservationTests`, `AgentInteropTests` (the client and server name one schema).
- **`loadout` op:** `{"op":"loadout","seat":3,"kit":{"primary":"launcher","magazines":3,
  "grenades":2,"armour":"medium"},"when":"locker"|"spawn"}` → applied, or the refusal code of §6.2.
  `locker` goes through §5.3; `spawn` sets the pending kit.
- **`reset`** restores each attached seat's kit to its configured one (its `attach`'s `kit`, else
  the default), so episodes start alike (AGENT_API.md §7.1).
- **Events:** `kit` (peer, the kit, `locker` or `spawn`); `damage.absorbed`; `kill.rounds_fired`
  (L0: the victim's primary rounds this life).
- **Harness:** a seat's `"kit"` field (sent with the `spawn` op, which gains `kit` and equips it,
  harness-only like the rest of `spawn`); a script step's `"loadout"` (sent once on the step's first
  tick, as a strategist step's `commands` are); metrics over the new fields.

| Scenario | Phase | Claims |
|---|---|---|
| `locker_launcher_vs_tank.json` (migrated) | L2 | Unchanged claims. The seat spawns at (8, 1, 10.0), 0.75 m from the footprint (it was 1.25 m at z = 9.5); its script's use tap becomes a `loadout` step for a launcher with 3 pouches (7 rounds, enough for the four the lane fires). |
| `locker_kit_refused.json` | L2 | A seat 3 m from the locker asks for a launcher kit and is refused; every `damage` record it causes names the rifle. |
| `magazines_run_dry.json` | L1 | A rifle seat with no pouches holding the trigger at a unit fires 30 rounds and no more: `damage` count ≤ 30, `weapon.reserve` 0, magazine 0 at the end. |
| `armour_vs_rifleman.json` | L3 | Two lanes, a rifleman 30 m from each still seat, one with no vest and one heavy: the heavy seat takes ≥ 1.6× the `damage` records to die, over seeds; `absorbed` > 0 only in its lane. |
| `weight_speed.json` | L4 | Three seats, stripped, default and heaviest, sprint the same line for 3 s: distances in the ratio 1.12 : 1.00 : 0.70 within 3 %. |
| `locker_resupply.json` | L5 | A seat fires 60 rounds 0.5 m from the locker, stops: reserve is back to 150 within 2 s of the last shot. A medium seat shot down to 0 armour walks to the locker: 60 points within 3 s of reaching it, and none while a rifleman keeps hitting it. |

## 10. Phases

Estimates in solo-dev days, as the other plans. "Engine-side" claims are headless Godot runs
through the harness; "listen host" and "dedicated server" need a person at the controls.

### L0 — Measure today (½ day)

1. `PlayerCombat.PrimaryRoundsThisLife`, reset at spawn, incremented in `ServerSimulate` when the
   large slot fires; reported as the `kill` event's additive `rounds_fired`.
2. Twenty bot-only rounds, `--bot-ai htn` and twenty `legacy`, recorded with the event stream:
   distribution of `rounds_fired` per large weapon; ground deaths per round by killer kind; mean
   ground speed of moving bots.
3. Record the numbers here under **L0 — result**; apply L-D3's rule to the default magazines.

- **Done when:** the p95 of rifle, DMR and launcher rounds per life is written down with its
  sample size, and the default kit is confirmed or raised.
- **Result — done; the default kit is raised to five magazines, and the pouches to eight.**
  - **The instrument.** `PlayerCombat.PrimaryRoundsThisLife` counts the rounds the large slot fires,
    reset by `Equip` — every spawn — and not by a locker's swap. A player's kill carries it as
    `rounds_fired`, the kill's fifth slot, and a unit's or a structure's as −1
    (`AgentEventSchema.NoRoundsFired`); the harness's field table and AGENT_API.md §8 name it. The
    server's kill line ends `| rifle 47 rounds this life`.
  - `dotnet test`: 2176 passed, 0 failed (2175 before; 1 new in `AgentEventTests`, and the kill's
    serialization test now pins the field). With the slot's name taken out of the schema, three
    tests fail. `dotnet build Gdpyr.csproj`: 0 warnings, 0 errors; the harness builds.
  - **Bot-only rounds** (Godot 4.6 .NET, headless, through the harness, as COVER.md §9 and TACTICS
    T8): five ground bots and the idle seat holding still at (110, 1, 110) against one computer
    strategist, 21,600 ticks (6 min) after a 60-tick warm-up, seeds 1–20 under `--bot-ai htn` and
    1–20 under `legacy`, units' micro-autonomy at its default (on); one server a round, six at a
    time, each held by the harness's steps, so the load did not change a round. Each round is read
    from its start to its first `round_end`, else to the run's end; the scenario files are not
    committed. Beside the committed instrument, a temporary probe, not committed, logged each
    bot's rounds this life by weapon, and its horizontal displacement every tick. The bots carry
    what `BotDirector.Equip` gives by roster slot, the seat taking slot 0 and the strategist slot 1:
    slots 2–6 are launcher, DMR, rifle, launcher, rifle — two rifles, one DMR and two launchers,
    whence the DMR's smaller samples.
  - **The instrument agrees with the probe**: all 1,083 player deaths in the windows (834 `htn`,
    249 `legacy`) carried the `rounds_fired` the probe counted. The seat never died and never fired,
    and is left out below. 14 of the 20 `htn` rounds ended early, the ground force out of its 50
    tickets at 213–343 s; no `legacy` round did.
  - **Rounds fired per life**, by the large weapon carried at the end of the life (in two `htn`
    lives a locker swap put a second weapon in the slot mid-life, and the counts below are the
    last weapon's; in every other life they are `rounds_fired`). Percentiles by nearest rank.
    "Deaths" are the lives a kill ended, which is what `rounds_fired` reports; "all lives" adds the
    bots alive at the window's end, whose counts are lower bounds. "Dry" is the share of all lives
    that fired more than the magazine and that many pouches hold (rifle 30 + 30 a pouch, DMR
    10 + 10, launcher 1 + 2).

    | Weapon · AI | Deaths: n · median · p90 · **p95** · max | All lives: n · p95 | Dry at 3 pouches | … at 5 |
    |---|---|---|---|---|
    | Rifle · `htn` | 329 · 30 · 126 · **170** · 310 | 363 · 177 | 11.0 % | 4.7 % |
    | DMR · `htn` | 176 · 8 · 40 · **57** · 110 | 193 · 51 | 8.8 % | 3.1 % |
    | Launcher · `htn` | 329 · 3 · 9 · **11** · 17 | 364 · 11 | 11.5 % | 3.3 % |
    | Rifle · `legacy` | 22 · 10 · 82 · **93** · 94 | 62 · 94 | 0 | 0 |
    | DMR · `legacy` | 7 · 10 · 26 · **26** · 26 | 27 · 26 | 0 | 0 |
    | Launcher · `legacy` | 220 · 1 · 3 · **4** · 16 | 260 · 9 | 6.5 % | 4.2 % |

  - **Ground deaths per round by killer**, mean (range) over the 20 rounds; "a ground player" is a
    blast of the victim's own or a teammate's.

    | Killer | `htn` | `legacy` |
    |---|---|---|
    | Infantry | 25.3 (4–38) | 7.0 (0–11) |
    | Tank | 13.5 (8–23) | 5.2 (3–9) |
    | Technical | 1.9 (0–10) | 0 |
    | A ground player | 0.65 (0–3) | 0.3 (0–1) |
    | Heavy gunner | 0.25 (0–3) | 0 |
    | Pillbox | 0.2 (0–4) | 0 |
    | Barracks gun | 0 | 0.05 (0–1) |
    | **All** | **41.7 (14–50)** | **12.5 (4–17)** |

  - **Ground speed of moving bots**, from each bot's horizontal displacement per tick over 26,300
    (`htn`) and 35,100 (`legacy`) bot-seconds alive; moving is above 0.5 m/s, and a displacement
    above 30 m/s (a teleport) is not counted.

    | | `htn` | `legacy` |
    |---|---|---|
    | Share of time alive spent moving | 56.9 % | 75.2 % |
    | **Mean speed while moving** | **4.51 m/s** | **4.51 m/s** |
    | … of it below 3 m/s · 3–6 · above 6 (sprinting) | 18.3 · 69.8 · 11.9 % | 20.0 · 66.4 · 13.6 % |
    | Share of time alive with a carry scale below 1 | 0 | 0 |

  - **What that says.** Under `htn`, the default bot AI, the planned default's 120 rifle rounds would
    have run out in 11 % of rifle lives, and the p95 of rifle rounds per life is 170 (329 deaths;
    a bootstrap of the p95 puts 90 % of resamples at 149–208, four to six magazines). L-D3's rule
    applies, and five magazines (180) is the fewest that cover the p95; they cover the all-lives p95
    of 177 too. Five pouches also bring the DMR and the launcher, under either AI, below the 5 % of
    lives L7 allows to end dry (§10 L7); at three, every `htn` weapon would fail that. `legacy` fights
    less — 12.5 ground deaths a round against 41.7 — and fires less: its rifle p95 is 93, inside
    the planned default, and 88 of its 220 launcher deaths came before the launcher fired. Speed:
    bots move at a mean 4.51 m/s while moving, under either AI, and never carried a gun or a can in
    these rounds, so after L4 the kit's scale is the only one a bot's walk has below 1; the default
    kit's is 1.00 by L-D4's construction, so these numbers stand until a kit differs from it.
  - **What changed in this plan.** L-D3: the default kit is rifle, five magazines, three grenades,
    no armour. L-D1: eight pouches rather than six, so that kit keeps today's three grenades and
    still fills them; the alternative — six pouches, five magazines and one grenade — takes two
    grenades from every default kit and every bot, which L-D3 keeps for today's lethality. **This is
    the decision to flag before L1** if a six-pouch budget matters more than the three grenades.
    L-D4: `W_ref` 9.3 kg. L-D13 and §6.1: the kit byte's magazine count takes the spare bit (0–8),
    and the reserve byte holds a rifle's eight pouches (240). §4.1's kit table, §7.2–7.4's
    examples and §9's `locker_resupply` reserve follow.
  - **Not taken**: L0 has no listen-host or dedicated-server check. The per-weapon, censored-life
    and speed figures came from the probe, which is not committed; every death, its killer and its
    `rounds_fired` are also in the committed kill events and kill lines.

### L1 — Loadout model, finite primary ammunition, the player record v2 (2 days)

1. `Loadout.cs` (§4.1): `LoadoutSelection`'s new bytes, `Sanitize`, `CarryStats`, weights,
   `SpeedScale` — computed but not yet applied to speed. `Loadout.NoPrimary` and its alias
   `WeaponCatalog.None`; `NameOf(None)` is `—`.
2. `CarryStats` exported on `WeaponDefinition`; the rifle, DMR and launcher `.tres` get §4.1's
   numbers; `WeaponCatalog.CarryFor`.
3. `WeaponState.Reserve`, `Empty`, the reload rule, `SelectSlot`'s mask (§4.2).
4. `PlayerCombat.Equip`/`Respawn` from the whole kit; no primary starts on the pistol; grenades from
   the kit.
5. Record v2 (§6.1) with the armour bytes written as zero; `KitFlags`; `ApplyAuthoritative`;
   `DemoFormat.Version` 4.
6. HUD: magazine / reserve, dry, `—`.
7. Observation JSON: `self.grenades`, `weapon.reserve`, `weapon.reserve_max`.
8. Scenario `magazines_run_dry.json`; harness `kit` seat field and `spawn` op `kit`.

- Tests: `LoadoutTests`, `WeaponSimTests` additions, `SnapshotCodecTests` (32 B round trip, the
  kit byte, the no-primary zero, malformed input), `DemoCodecTests` (version 4, 3 refused),
  `ScenarioTests` (the `kit` field's parsing and refusals).
- **Done when:** `dotnet test` passes; `dotnet build Gdpyr.csproj` has no warnings; the whole
  scenario suite passes as before plus `magazines_run_dry`; in a listen-host round the reserve
  counts down, a reload with none spare does not start, a no-primary kit (set from the console)
  spawns on the pistol and `3` does nothing.

### L2 — The kit request: one validator, the use key, the RPC, the agent op (1½ days)

1. `UseAction.OpenLocker`; `LockerSim`, `LockerSwap`, `EmplacementManager.SwapWeapon` and the swap
   prompt removed; the locker's footprint and yaw registered; `LockerReachMeters` 1.0 from the
   footprint (`LockerSupply.FootprintDistance`, §4.4, lands here).
2. `PlayerCombat.ApplyKit`; `CombatManager.TryApplyLockerKit` (§5.3); `ClientLockerLoadout`,
   `ServerKitResult`, `ClientSelectLoadout` widened.
3. A temporary console command, `kit <primary> <mags> <grenades> <vest>`, calling
   `RequestLockerKit`, so L2–L5 are playable before the panel.
4. Agent `loadout` op, `reset` restoring kits, the `kit` event; script step `loadout`.
5. Migrate `locker_launcher_vs_tank.json` and `scripts/locker_then_fire.json`; add
   `locker_kit_refused.json`.

- Tests: `EmplacementTests` (renamed rows), validator cases in a new engine-free
  `KitRequestTests` over a `KitSituation` struct (each refusal, the slack, the 0.25 s limit),
  `LockerSupplyTests` (footprint), `AgentCodecTests` (the op and its refusals), `ScenarioTests`.
- **Done when:** both locker scenarios pass engine-side over their seeds; the full suite passes; on a
  listen host and a dedicated server, `kit launcher 3 0 none` at the locker swaps the weapon and is
  refused 3 m away, and a remote client sees the new weapon.

### L3 — Body armour (1 day)

1. `BodyArmour.cs` (§4.3); `PlayerCombat.Vest`, filled at spawn and by `ApplyKit`.
2. `Damage(…, explosive)` at its three call sites; `damage.absorbed`; `LastDamagedTick`.
3. The record's armour points and the kit byte's vest bits; HUD `vest`; observation `self.armour`,
   `armour_max`, `armour_tier`.
4. The remote chest band (§7.5).
5. Scenario `armour_vs_rifleman.json`.

- Tests: `BodyArmourTests` (the hits-to-kill table and the edge cases), `AgentEventTests`
  (`absorbed`).
- **Done when:** the armour scenario passes over its seeds; every pre-existing scenario still passes
  (no bot and no default seat wears a vest); on a listen host a player in a heavy vest standing in
  front of a pillbox takes 5 of its rounds to kill, as §4.3 says, where an unarmoured one takes 3.

### L4 — Weight and movement speed (1 day)

1. `PlayerManager.Simulate` multiplies the carry scale by the kit's (§5.5); cache on `PlayerCombat`.
2. Demo playback applies the kit scale (§6.3).
3. HUD `load`; observation `self.load_kg`, `self.load_scale`; the agent `move_speed_scale` stays as
   it is.
4. Scenario `weight_speed.json`.

- Tests: `LoadoutTests` (already the curve); a `MovementTests` case driving `Movement.ApplyMove` at
  1.12 and 0.70 to the speeds of §4.1.
- **Done when:** `weight_speed` passes; the debug HUD's mispredictions per second on a listen host
  and over a 100 ms `tc netem` link (DEPLOYMENT.md §4) are no higher in a heavy kit than in the
  default, except in the half-second after a kit is taken; the default kit's sprint measures 8.0 m/s
  as before.

### L5 — Resupply within 1 m (1 day)

1. `LockerSupply.Step` (§4.4) and the server pass (§5.4).
2. HUD resupply prompt; the debug HUD's `kit` row; the floor ring and label (§7.5).
3. Grenades: NETCODE §10.9's "a weapon locker does not refill them" becomes "the locker's resupply
   does".
4. Scenario `locker_resupply.json`.

- Tests: `LockerSupplyTests` (pacing, lockout, ceilings).
- **Done when:** `locker_resupply` passes over its seeds; on a listen host a player standing at the
  locker sees magazines, grenades and vest come back at the stated pace and nothing while being
  shot.

### L6 — The locker panel and the death screen (2 days)

1. `LoadoutPanel.cs` (§7.2): rows, steppers, read-outs, keys, mouse.
2. `InputFocus.Menu`, the sampler's neutral frames, mouse release and recapture, `PauseMenu`'s
   `Esc` ordering.
3. Local open from `ClientSimulateLocal`'s use tracker; the auto-close conditions;
   `ServerKitResult` handling.
4. Death-screen line and next-spawn mode (§7.3); `LoadoutMenu.cs` deleted; the `kit` console
   command removed.
5. README's controls and the locker paragraph rewritten.

- Tests: the panel's model is a plain class (`LoadoutDraft`: the kit being edited, what each key
  does to it, what is disabled, what each `ServerKitResult` code does to it) under `Scripts/Sim`, so
  `LoadoutDraftTests` covers every row, limit and refusal without a window; the widget only draws
  it.
- **Done when:** on a listen host and against a dedicated server: `E` at the locker opens the panel
  and the character stops; every row works with the mouse and with the keys; `Enter` applies and
  closes; `Esc` closes the panel and not the game; the console still types; dying with it open
  closes it and takes nothing; the death screen's `E` edits the next spawn's kit and the next spawn
  has it; a second client sees the vest band and the weapon change.

### L7 — Computer players (1½ days)

1. Dry → pistol (§8), both AIs.
2. `GroundFact.Ammo`, `Rearm`, the locker runner's kit request; the debugger's map.
3. `BotKits = role` and its kits; a bot's kit in the inspect panel.
4. Run the `htn_*` scenarios with `BotKits = role`, and ten bot-only rounds each way.

- Tests: `BotBrainTests` (dry switches, comes back), `GroundHtnTests` (`Rearm` planned when dry
  and nothing in sight, not while a contact is), `GroundCoordinatorTests` (the runner),
  `HtnDebugTests`.
- **Done when:** every `htn_*` scenario passes under `default` and under `role`; `role` becomes
  the game mode's default only if it also leaves the ground force's tickets-lost per minute in
  bot-only rounds within the run-to-run spread L0 measured, otherwise it stays opt-in and the
  numbers are recorded here; the share of bot lives that end with the primary dry
  (`rounds_fired` against the kit's rounds) is recorded, and is under 5 % with the default kit.

### L8 — Agent schema obs-4, measure, document (1½ days)

1. The flat vector's five floats; `gdpyr-agent-obs-4`; the Python client and the C# client.
2. Re-measure the legacy baseline (TRAINING.md §9) and record it.
3. Docs: NETCODE §7 (record, messages), §10.6 rewritten as the loadout and the locker, §10.9's
   grenade line; AGENT_API §6.1, §7.1, §7.2 (the grenade-count sentence), §8, §9.1; DEMOS (version
   4); HTN_BOTS §5.1 (`Rearm`, the runner); IMPLEMENTATION_PLAN §M2 and M11; README; this file's
   status and each phase's result.

- **Done when:** `dotnet test`, `dotnet build Gdpyr.csproj`, the agent client and the trainer build;
  the full scenario suite passes; a 500-episode PPO smoke run against obs-4 starts and logs, as
  TRAINING.md §3 describes.

**Total: ~12 days.**

## 11. Verification

```bash
dotnet build Gdpyr.csproj
dotnet test
./scripts/playtest.sh Tests/Scenarios/*.json --json
godot --path . -- --listen            # and a second client: --client 127.0.0.1
godot --path . --headless -- --server # dedicated, with two clients
```

Each phase records under its heading what `dotnet test` counted, which scenarios ran on which seeds,
and which engine-side checks were and were not taken, as TACTICS.md's phases do.

## 12. Risks

| Risk | Mitigation |
|---|---|
| Finite ammunition changes every fight, the baseline's included. | L0 sizes the default from measured fire; pistol fallback and `Rearm`; legacy re-measured in L8. |
| The panel holds a player still with the mouse released; one shot from a sniper tower reaches the spawn. | The spawn is the ground force's; resupply's lockout already stops a refill under fire. If playtests disagree: close the panel on damage — one condition in §7.2's list. |
| `Esc`, the console and the panel fight over the keyboard. | `InputFocus` already arbitrates the console; the panel takes `Esc` first by `PauseMenu` checking it, which is one line and testable by hand in L6's list. |
| A kit change mispredicts speed for a round trip. | It happens at a locker or a spawn, a few times a life, and reconciliation absorbs it as it does a carry; L4 measures it. |
| The 32 B record breaks every demo recorded before it. | Refused with its reason, as T3's bump did; demos are not archived. |
| Heavy kits cannot slide (sprint ≤ 6 m/s). | Accepted as a cost of weight; if playtests disagree, the slide threshold becomes a fraction of the sprint speed. |
| Bots wander to the locker mid-fight. | `Rearm` only with nothing in sight; dry with a contact means pistol. |
| A map scales a locker; the footprint would be wrong. | `EmplacementManager` reads the footprint from the node's basis and refuses a scaled locker with an error at load. |

## 13. Not in this plan

- Money, prices or an economy for the ground force: weight is the only cost.
- Helmets, hit zones, armour-piercing rounds: players have one hitbox and rounds one damage.
- Dropping a weapon or a kit, or picking one up from the dead.
- A choice of sidearm, or no sidearm.
- Weight on jump height, stamina, aim speed or sway.
- The strategist side reacting to kits: units do not prefer the unarmoured, and no unit wears a vest.
- Resupply for players at a supply truck or a barracks; lockers anywhere but where the map puts them.
- Vest models beyond a greybox band.
