# Ground-force loadout: the locker panel, body armour and weight — implementation plan

Status: **L0 built and measured, L1–L7 built (§10; L1–L5's listen-host and dedicated-server checks not yet taken, L6's taken headless by a probe and not yet by a person; L7 measured in bot-only rounds, and its bots' dry share is over the 5 % it aimed for); L8 planned.** Planned from the code at `dd579dd`. Phases
L0–L8 (§10); decisions L-D1 to L-D15 (§3) are the defaults the plan is written against — flag any
you disagree with before L1 starts. L0's measurement raised the default kit to five magazines;
there are nine pouches, so it keeps three grenades (L-D1, L-D3, L-D4, L-D13; §10 L0's result).

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
| L-D1 | **A loadout is a primary, nine pouches and a vest.** Primary: none, DMR, rifle or launcher. Each pouch holds one reload of the primary or one hand grenade; at most three grenades. Vest: none, light, medium, heavy. The hammer and the pistol are always carried. | "Slots for extra primary ammunition or grenades" as one shared budget makes the trade the request describes. Three grenades is the snapshot's two bits and today's count. A stripped player still has a pistol, as Counter-Strike keeps the knife and pistol. Nine rather than the six first planned: the default kit L0 measured (L-D3) takes eight of them with today's three grenades, and leaves one free. |
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
| L-D13 | **The player record goes from 29 to 32 B** (§6.1): spare rounds and armour points in one 16-bit field (9 bits · 7), and a kit byte (vest 2 bits · magazine pouches 4 · grenade pouches 2). Zero in the large-weapon bits now means **no primary**. `DemoFormat.Version` 3 → 4; an older demo is refused with its reason. | The owner derives its whole kit — and so its weight and speed — from the record, the one thing a client is never wrong about for long; no new state message. +0.7 KB/s per client at eight players. Redesigned, not squeezed, as TACTICS T-D4 did the unit record. |
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

- `PouchSlots = 9`; `Sanitize(kit, ReadOnlySpan<byte> largeWeapons)`: an unknown primary is the
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
| Rifle, 9 mags, light | 13.1 | 0.92 | 4.6 / 7.4 |
| Rifle, 5 mags, 3 grenades, medium | 15.3 | 0.88 | 4.4 / 7.0 |
| DMR, 5 mags, 3 grenades, medium | 16.6 | 0.85 | 4.3 / 6.8 |
| Rifle, 5 mags, 3 grenades, heavy | 19.3 | 0.80 | 4.0 / 6.4 |
| Launcher, 9 pouches, heavy | 26.1 | 0.70 (floor) | 3.5 / 5.6 |

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
| 29–30 | — | one little-endian 16-bit field: **reserve**, the primary's spare rounds, in the low 9 bits (0–511; a rifle at nine pouches is 270, past a byte) · **armour points** in the high 7 (0–127; heavy is 100) |
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
│ POUCHES   ▣▣▣▣▣◆◆◆□  magazines [5 −] 5 [6 +]   grenades [7 −] 3 [8 +]     │
│ ARMOUR    [none]  [light 3 kg]  [medium 6 kg]  [heavy 10 kg]   [9 −][0 +] │
│                                                                          │
│ LOAD 15.3 kg   SPEED 88 %  ██████████████░░   walk 4.4 · sprint 7.0 m/s  │
│ VEST 60 pts, absorbs 50 %   rifle hits to kill 8 (5 without)             │
│ AMMO 30 + 150 rifle · 3 grenades                                         │
│                                                                          │
│ [Enter] take this kit   [Backspace] what I carry   [D] default   [E] close│
└──────────────────────────────────────────────────────────────────────────┘
```

- **Choices.** Primary and vest are radio rows. Pouches are two steppers over nine cells drawn
  magazines-first — only the counts mean anything; `+` is disabled at nine pouches, grenades at
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
- **Result — done; the default kit is raised to five magazines, and the pouches to nine.**
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
    no armour. L-D1: nine pouches rather than six, so that kit keeps today's three grenades, with
    one pouch to spare; six would have left room for one grenade beside five magazines, taking two
    from every default kit and every bot, which L-D3 keeps for today's lethality. L-D4: `W_ref`
    9.3 kg. L-D13 and §6.1: the kit byte's magazine count takes the spare bit (0–9), and the reserve
    and the armour points share bytes 29–30 (9 bits and 7), because a rifle's nine pouches are 270
    rounds, past a byte; the record stays 32 B. §4.1's kit table, §7.2–7.4's examples and §9's
    `locker_resupply` reserve follow.
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
- **Result — built.** Two pieces of later phases came forward, and the observation fields reach a
  packed observation by a route the plan did not name; each is said below.
  - **The kit** (`Scripts/Sim/Loadout.cs`): `BodyArmourTier` (the enum only; what a vest does is
    L3's), `CarryStats`, `Loadout.NoPrimary` (255) and its alias `WeaponCatalog.None` (whose name is
    `—`), `PouchSlots` 9, the default kit's five magazines and three grenades, `Sanitize`,
    `WeightKg`, `SpeedScale`, `ReserveFor`, and `TryParse` for a kit spelt out by name.
    `SpeedScale(weight, reference)` takes the reference from the catalog —
    `WeaponCatalog.ReferenceWeightKg`, the default kit weighed with the `.tres` numbers at load —
    rather than a written-down 9.3, so the default kit is exactly 1 however the weights are tuned;
    `LoadoutTests` pins 9.3 and §4.1's table. Speed is computed, by the console's `loadout` line,
    and not yet applied (L4). `LoadoutSelection` gained `Magazines`, `Grenades`, `Armour` and an
    equality.
  - **Carry stats**: `CarryWeightKg`, `PouchRounds` and `PouchWeightKg` on `WeaponDefinition`; the
    rifle 3.6 / 30 / 0.5, the DMR 4.4 / 10 / 0.6, the launcher 6.0 / 2 / 0.9; `WeaponCatalog.CarryFor`.
  - **Finite primary ammunition**: `WeaponState.Reserve` (−1 unlimited, what `Ready` still gives by
    default, so every unit, mounted gun and the pistol reload forever), `Empty`, `IsEmpty`; a reload
    moves `min(missing, reserve)` and needs a reserve that is not 0; an empty slot never fires;
    `SelectSlot(current, input, occupied)`, the mask from `PlayerCombat.OccupiedSlots`; and
    `SimConfig.SidearmSlot`.
  - **Equipping the kit**: `PlayerCombat.Equip` arms the primary with its pouches' rounds spare, the
    grenades from the kit, and the primary up or — with none — the pistol. Until L2 retires it, the
    locker's swap gives the new primary full pouches, L-D10's rule for a different primary. Bots keep
    their slot's primary and take the rest of the default kit.
  - **The record v2**: 32 B, as §6.1 — bytes 29–30 the reserve (9 bits) and the vest's points (7,
    written 0 until L3), byte 31 the kit (`KitFlags`); a zero large weapon is no primary. A client's
    `Loadout` is the record's, and its reserve is applied under the magazine's acknowledgement
    guard. `DemoFormat.Version` 4; versions 1–3 are refused with their reason.
  - **HUD**: `rifle 30 / 150`, `pistol 12 / ∞`, `dry`, `—`, as §7.4.
  - **The observation fields**: `self.grenades`, `weapon.reserve` and `weapon.reserve_max` (−1 for a
    weapon that reloads forever), in raw units, as the ground schema's `extras` block
    (`AgentObservation.ExtraFields`). Every JSON observation names them, as planned; but the
    playtest harness, the .NET client and the Python client all take packed observations, which
    the plan's JSON-only fields would never reach. So a packed observation carries them too, after
    the vector and any planes with flag bit 3, when the session's `hello` asks (`"extras": true`).
    The vector, `gdpyr-agent-obs-3` and every frame a client that does not ask receives are
    unchanged until L8; the harness asks (AGENT_API.md §6.1).
  - **The harness**: a seat's `"kit"`, checked when the file is read against limits
    `ScenarioTests` pins to the game's, and sent with the seat's `spawn`, which equips it and makes
    it the respawn kit, refusing a kit that breaks a rule (`invalid`); a `spawn` with no `at` only
    equips. Scenarios `magazines_run_dry.json` and, beyond the plan, `no_primary_pistol.json`.
  - **From L2: `ClientSelectLoadout` carries the whole kit**, next-spawn only, and a console command
    `loadout <primary> <magazines> <grenades> [armour]` sets it. Needed here twice over: this phase's
    check sets a no-primary kit from the console, and the old one-byte request, sanitized under the
    new rules, would have dropped the kit's pouches whenever `1`/`2`/`3` picked a weapon on the
    death screen. `RequestLoadout(byte)` now keeps the rest of the pending kit.
  - **From L7: a dry primary is put away for the pistol, under either AI.** Finite ammunition alone
    failed `htn_ground_denies_node` in both full suite runs on the first build of this phase — one
    seed each, the first, with one node captured where the claim wants two — while the build before
    it passed. A temporary probe (not committed) logging each bot's first dry fire found the cause
    when that file was run as the suite runs it, after `builder_fortifies`: the rifle bot that had
    killed the far node's first rifleman at tick 2178 dry-fired from tick 2931, all 180 of its rounds
    spent, and the DMR bot from 4068, all 60 of its; the second rifleman lived to 5288. Before this
    phase the same bot, reloading forever, killed it at 3916. Run on its own the file passed 32 of
    32 seeds and no bot ran dry: the rounds are not reproducible seed for seed (COVER.md §9), and
    run alone both of the far node's riflemen were dead by tick 2104. `BotSituation` now carries the slot in hand and
    `PrimaryDry` (nothing in the magazine, nothing spare, or no primary); `BotBrain` presses
    `Weapon2` with the primary up and dry and `Weapon3` once it has rounds. On that build the file
    passed in the suite's order four times out of four with the probe, and in the final suite run
    below — but in none of the probed runs did a bot run dry, so the fallback has been checked by
    `BotBrainTests` and not yet seen working in a round.
    `Rearm` and per-role kits stay L7's.
  - `dotnet test`: 2239 passed, 0 failed (2176 before; 63 new: 26 in `LoadoutTests`, 7 in
    `WeaponSimTests`, 5 in `SnapshotCodecTests`, 1 in `DemoCodecTests`, 3 in `AgentObservationTests`,
    6 in `AgentCodecTests`, 12 in `ScenarioTests`, 3 in `BotBrainTests`). `dotnet build
    Gdpyr.csproj`: 0 warnings, 0 errors; the harness and the .NET client build.
  - **The tests test something.** One change at a time, each failed tests: a reload that ignored the
    reserve (4), a reload with nothing spare (1), the slot mask ignored (1), an empty slot that fires
    (1), grenades trimmed before magazines (3), magazines kept with no primary (2), the kit byte's
    magazines a bit out of place (1), the speed line's slope at 2.5 % (5). A ten-bit reserve, which
    overlaps the vest's points, failed nothing until `TheSparesTwoFieldsNeverReachIntoEachOther` was
    added; it fails that one.
  - **Scenarios** (Godot 4.6 .NET, headless, through the harness, default `htn`): the full suite on
    the final build, 32 of 32 over 69 seeds — the 30 before this phase, `magazines_run_dry` (30 hits
    on a sandbag wall and none after, over both seeds; with two spare magazines the same file
    records 66 and fails) and `no_primary_pistol` (14 pistol hits, none from the rifle, the pistol
    up at the end; with a rifle kit the same file fails five of its claims). The build before this
    phase ran the 30 beside the first run and passed all 30.
  - **Not taken**: the listen-host round. The headless scenarios cover what its list asks of the
    simulation — the reserve running out, no reload with none spare, a no-primary kit spawning on the
    pistol with `3` doing nothing — but through the `spawn` op, not the console; the console
    command, the HUD's lines and a remote client's prediction of its own reserve have not run in
    front of anyone.

### L2 — The kit request: one validator, the use key, the RPC, the agent op (1½ days)

1. `UseAction.OpenLocker`; `LockerSim`, `LockerSwap`, `EmplacementManager.SwapWeapon` and the swap
   prompt removed; the locker's footprint and yaw registered; `LockerReachMeters` 1.0 from the
   footprint (`LockerSupply.FootprintDistance`, §4.4, lands here).
2. `PlayerCombat.ApplyKit`; `CombatManager.TryApplyLockerKit` (§5.3); `ClientLockerLoadout`,
   `ServerKitResult`. (`ClientSelectLoadout` was widened in L1.)
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
- **Result — built.** The locker runner's kit request came forward from L7, because this phase
  retires the swap it tapped for; the `kit` event, `reset`'s kit and one scenario differ from the
  plan's words, and a third scenario had to move. Each is said below.
  - **The reach** (`Scripts/Sim/LockerSupply.cs`): `FootprintDistance` — flat, to the 1.2 × 0.5 m
    cabinet turned with its node, 0 inside it — `InReach`, and `StandPoint`, 0.5 m in front of the
    racks. `SimConfig.LockerReachMeters` is 1.0 from the footprint; guns and cans keep 2.5 m.
    `WeaponLocker` takes its size from there and its yaw from its basis's +Z, and
    `EmplacementManager` leaves out a locker a map has scaled or tilted, with an error (§12).
    `LockerSupply.Step`, the resupply, is L5's.
  - **The use key**: a tap at a locker is `UseAction.OpenLocker`, which the server resolves and does
    nothing with — the panel is L6's. `LockerSim`, `WeaponCatalog.LockerSwap`, `SwapWeapon`,
    `PlayerCombat.SwapLarge` and `LockerSwaps` are gone; `LockerSim.IndexOf` is `Loadout.IndexOf`.
    The prompt names the console command until the panel: `weapon locker   [~] kit <primary>
    <magazines> <grenades> <vest>`.
  - **One validator** (`Scripts/Sim/KitRequest.cs`, `CombatManager.TryApplyLockerKit`): §5.3's rule
    as `KitRequest.Check` over a `KitSituation`, refusing in the order invalid, dead (which a
    strategist is), mounted, carrying, not at the locker (1.0 m + 0.5 m slack), too soon (a
    quarter-second after the last kit taken; refusals do not count). `Loadout.IsValid` is
    "`Sanitize` would change nothing the asker chose". On success `PlayerCombat.ApplyKit` — L-D10 as
    `Loadout.RefitPrimary` and `RefitGrenades`: a different primary full, the same one keeping its
    magazine and its spare rounds up to the new pouches, grenades up to theirs, added pouches empty
    — `PendingLoadout` (L-D11), the `kit` event and a `[locker]` log line; the round-over line
    counts kits taken.
  - **The wire**: `ClientLockerLoadout` (four bytes, not sanitized before the validator, so a kit
    the client did not mean is refused rather than trimmed) and `ServerKitResult` (the refusal
    byte) to the requester, which prints it on the console until the panel shows it. The record
    already carries everything a kit changes, so nothing else is sent. A console command, `kit
    <primary> <magazines> <grenades> <armour>`, asks through `RequestLockerKit`, applied directly on
    the authority as `RequestLoadout` is.
  - **The agent API**: the `loadout` op, `when` `locker` (through the validator) or `spawn` (the
    pending kit). A refusal is an answer — `"applied": false` and §6.2's code — not an error frame;
    a kit of the wrong shape is `bad_request` and one that breaks a rule `invalid`, as for `spawn`.
    `attach` takes a `kit` too. **`reset` restores** each attached ground seat's kit: its
    `attach`'s, else its last `spawn`'s, else — where §9 says "the default" — the kit its bot
    carried when it was attached, which is the default kit but for a roster slot's primary, so a
    seat on a DMR bot's slot keeps the DMR it has had since L1. The .NET client has `LoadoutAsync`
    and a `kit` on `AttachGroundAsync`; the Python client `loadout()` and a `kit` argument.
  - **The `kit` event** has five slots, as every event does — peer, primary (−1 for none), armour,
    magazines, grenades — and §9's sixth value, `locker` or `spawn`, would not fit. It is emitted
    for a kit taken at a locker, by a person, a bot or a policy; a kit kept for the next spawn
    changes nothing in the round until that spawn, and emits nothing.
  - **The harness**: a ground step's `"loadout": {"kit": {...}, "when": "locker"}`, sent once on the
    step's first tick and answered before the tick is stepped; `counters.loadouts_applied`,
    `counters.loadouts_refused` and `counters.loadouts_refused.<code>`, the codes pinned to the
    game's by `ScenarioTests`; every answer in the trace.
  - **From L7: the locker runner asks for a kit.** With the swap gone, an HTN runner that tapped at
    the locker would have given up every trip. It walks to `StandPoint`, is there 0.8 m from the
    footprint (`GroundPlanTraits.LockerArriveMeters`, measured from it now, not 2 m from the
    origin), and the pilot asks the validator for its own kit with a launcher; a refusal sets
    `GroundContext.KitRefused`, and `UseLocker` gives the trip up as a timeout does
    (`FailedLockerTrips`). `GroundIntent.UseLocker` is `TakeLauncher`; `BotSituation.Use`,
    `UseTapIntervalTicks` and the pilot's tap are gone. **The launcher is for that life**: the
    first probe of it found a runner whose kit became its next spawn's (L-D11) respawning with the
    launcher after `reset`, so that in seeds 2 and 3 two launchers were already in hand and no
    runner was sent. L-D14 keeps bots in their roster kit until L7, so the pilot puts its
    `PendingLoadout` back after the kit is taken.
  - **Scenarios.** `locker_launcher_vs_tank.json`: the seat at (8, 1, 10.0), 0.75 m from the
    footprint, and its script's tap a `loadout` step for a launcher with three pouches; it now also
    claims one kit taken, a launcher with three magazines. `locker_kit_refused.json` (new): 3 m out,
    refused `not_at_locker`, no `kit` event, every `damage` record the rifle's, and the kit still a
    rifle's (`weapon.reserve_max` 150). **`technical_wreck_ejects.json`**, not in the plan, tapped the locker
    from (8, 1, 9.5), 1.25 m from the footprint and outside the new reach: its grenadier now takes
    a launcher kit with three pouches as a seat `kit`, and its script, the tap an idle step of the
    same length, is `scripts/six_rounds.json`. **`htn_locker_runner_kit.json`** (new, beyond the
    plan): a roster of three — the scenario's seat, idle far off, an HTN DMR bot and a launcher bot —
    and a tank 45 m from the spawn; the DMR bot is sent to the locker and takes a launcher kit, in
    every seed.
  - `dotnet test`: 2285 passed, 0 failed (2239 before; 46 more: 12 in `KitRequestTests`, 12 in
    `LockerSupplyTests`, 11 in `LoadoutTests`, 10 in `ScenarioTests`, 3 in `GroundHtnTests`, 2 in
    `AgentEventTests`, 1 in `AgentCodecTests`, and 5 fewer in `EmplacementTests`, whose locker-cycle
    tests went with `LockerSim`). `dotnet build Gdpyr.csproj`: 0 warnings, 0 errors; the harness and
    the .NET client build.
  - **The tests test something.** One change at a time, each failed tests: no latency slack (1), no
    quarter-second (1), a footprint not turned with its yaw (1), the same primary refilled (2),
    grenade pouches refilled (1), the vest left out of `IsValid` (1), the runner ignoring a refusal
    (1), a tap at a locker doing nothing (2), the runner's arrival measured from the origin (1).
  - **Scenarios** (Godot 4.6 .NET, headless, through the harness, default `htn`): the full suite on
    the final build, 34 of 34 over 74 seeds — the 32 before this phase and the two new ones — in
    two runs, 30 files and then the last four, because the container restarted between them.
    `locker_launcher_vs_tank`: one kit taken, a launcher with three magazines, and four launcher
    hits on the tank in each seed, as before the phase. `locker_kit_refused`: refused
    `not_at_locker` and 60 rifle hits on the wall in each seed. `technical_wreck_ejects`: all four
    riders out alive. `htn_locker_runner_kit`: five kits taken over three seeds, each a launcher by
    the DMR bot, which in a run of the same file on the same build never respawned with one (its
    server log); on the build before the runner's next spawn was
    put back, the same file took one kit, in seed 1, and failed seeds 2 and 3.
  - **Not taken**: the listen-host and dedicated-server checks. The headless scenarios cover the
    validator from the agent op and the bot — a kit taken at 0.75 m, refused at 3 m, the new
    primary's rounds and blasts in the record and the events — but not the RPC from a client, the
    console command, the prompt, or a remote client's view of the new weapon.

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
- **Result — built; nothing engine-side run.** The scenario is written and parses, but has not run,
  and neither has the suite against this build nor the listen host; that is said last. One harness
  feature came with the scenario, and the vest is drawn in a demo too; each is said below.
  - **The rule** (`Scripts/Sim/BodyArmour.cs`): `BodyArmourState` (tier and points, `Full`), and
    `BodyArmour.Share`, `MaxPoints` and `Absorb`, §4.3's function: the share, halved for an
    explosive, of the hit, rounded to a whole point — a half point to the vest
    (`MidpointRounding.AwayFromZero`; .NET's default, to even, would give a medium vest 22 of a
    45-point round's 22.5, and the table holds either way) — and no more than the points left.
    `HitsToKill` steps a full vest and full health through `Absorb`, rounding each remainder as
    `PlayerCombat.ApplyDamage` does: the panel's read-out (§7.2), and what `BodyArmourTests` pins
    §4.3's table with, its rounds' damage read from their `.tres` files.
  - **The vest on the player**: `PlayerCombat.Vest`, full at every spawn. `ApplyKit` refits it with
    `Loadout.RefitVest`, L-D10's rule beside `RefitPrimary` and `RefitGrenades`: a different tier
    comes full, the same keeps its points — so asking a locker for the vest you wear refills
    nothing, which L5's resupply will. `LastDamagedTick` is set on every hit to a living player, a
    hit the vest took whole included, and cleared at a respawn, for L5's lockout; nothing reads it
    yet.
  - **Damage**: `CombatManager.Damage(…, explosive, …)` runs `Absorb` before `ApplyDamage` — the swing
    `false`, a round's contact `stats.IsExplosive` (the launcher's impact, as for `Armour`), a blast
    `true` — and a hit the vest took whole is still confirmed. With no vest `Absorb` returns the hit
    unchanged, so an unarmoured player's health, kills and events are what they were.
  - **The `damage` event**: `amount` is now the health lost — the hit, as before, for anyone without
    a vest — and `absorbed` is beside it. The event's five slots were all spoken for, so
    `AgentEvent` gained a sixth, `Z`, and every kind's slate six names, the sixth empty for every
    kind but `damage`; `WriteEvent` writes six. JSON-only and additive: a client that does not know
    `absorbed` never reads it. The harness's field table names it. The `kit` event's sixth stays
    unnamed: it is still emitted only for a locker's kit, so §9's `locker` or `spawn` would say
    one thing (L2).
  - **The record**: the high seven bits of bytes 29–30 carry the vest's points
    (`PlayerCombat.SnapshotArmour`); L1 already wrote the kit byte's vest bits. A client's
    `ApplyAuthoritative` takes the points whole in every snapshot, for every player: nothing on a
    client predicts damage, so there is no acknowledgement to wait on.
  - **HUD**: `HP 100   vest 60   grenades 3`; no vest says nothing about one, a spent one says 0.
  - **The observation**: `self.armour`, `self.armour_max` and `self.armour_tier` (0 none to 3 heavy),
    three more extras appended after L1's, so their offsets are 3–5 and L1's stay 0–2. The vector
    and `gdpyr-agent-obs-3` are unchanged.
  - **The chest band** (`fps_controller.SetVestPresentation`): a 0.4 m cylinder of radius 0.53 m
    round the 0.5 m placeholder capsule, khaki, olive or charcoal from light to heavy, hidden with
    no vest; a child of the collision shape, as the placeholder is, so it crouches and slides with
    it. Never on the character a process looks out of, nor on a dedicated server. Set every tick
    for every other player from `CombatManager.UpdateHud` — the record's kit on a client, the
    authority's own on a listen host — and, beyond the plan, from a demo's keyframes
    (`KitFlags.Vest` of the kit byte), since playback keeps no combat state.
  - **Beyond the plan: a filter may name a seat.** The scenario compares two seats' hits, and a
    seat's peer id is chosen by the server when it attaches; without a name for it, the two lanes'
    `damage` records could be told apart only by what the vest did to them, which is what the
    scenario measures. In an event filter, `@` and a seat's id is that seat's peer id —
    `events.damage[victim=@heavy].count` (AGENT_API.md §9). A seat the scenario does not have
    fails, as a field it does not have does; the test that parses every checked-in scenario
    registers each file's seats, so such a typo fails `dotnet test`.
  - **Scenario `armour_vs_rifleman.json`**: two lanes 50 m apart — no vest at x = −90, heavy at
    x = −40 — each a seat holding still at z = 80 in the default kit, and an infantry rifleman with
    no order 30 m south of it. Each rifleman is 58 m from the other lane's seat and at least 58 m
    from the ground spawns, past an infantry's 45 m sensor, so neither lane's records are the
    other's and a respawned seat is not shot again. Claims, over seeds 6 and 7: each seat killed
    once; 5 `damage` records on the bare seat and 8–10 on the heavy one (the plan's 1.6×, and §4.3's
    10); `absorbed` 0 throughout the bare lane, and above 0 and summing to the heavy vest's 100
    points in its own; the observation's vest fields, the heavy one worn to 0.
  - `dotnet test`: 2324 passed, 0 failed (2285 before; 39 new: 31 in `BodyArmourTests`, 5 in
    `LoadoutTests`, 2 in `AgentEventTests`, 1 in `ScenarioTests`; `AgentObservationTests`' extras
    and five tests that spell out an event's slate widened). `dotnet build Gdpyr.csproj`: 0
    warnings, 0 errors; the harness and the .NET client build.
  - **The tests test something.** One change at a time, each failed tests: a blast at the full share
    (2), no cap at the vest's points (6), a half point rounded to health (1), points never worn
    (12), the same vest refilled at the locker (1), the sixth slot not written (1), the vest's
    points not in their extra (1), a seat's name not resolved (1). The first build of `HitsToKill`
    bounded its loop by the health it was lowering; `AHeavierVestNeverDiesSooner` failed on it
    (a light vest against 20-point blasts read 0 hits).
  - **Not taken**: every engine-side check. Godot was not run in this session, so
    `armour_vs_rifleman` has not run over its seeds, the existing suite has not been run against
    this build, and the pillbox check on a listen host has not been made. Nothing an unarmoured
    player meets has changed — `Absorb` with no vest is the identity, and no bot or default seat
    wears one — but the only other change every scenario sees, an `absorbed: 0` on each `damage`
    record, has not been run either. The scenario's numbers come from §4.3's rule, which
    `BodyArmourTests` pins, and from its geometry; none has been seen in a round.

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
- **Result — built; nothing engine-side run.** The scenario is written and parses, but has not run,
  and neither has the suite against this build nor either misprediction check; that is said last.
  One harness feature came with the scenario, and the replay takes a new kit's speed at once; each
  is said below. The plan's "the agent `move_speed_scale` stays as it is" is read as the field: it
  now carries the kit's factor too, clamped as before.
  - **Speed** (`PlayerManager.MoveScaleOf`): the carry's scale times the kit's, set before every
    simulated tick, on the server and the owning client alike, as §5.5 says. The kit's is
    `PlayerCombat.LoadScale`, beside `LoadKg`, both recomputed wherever `Loadout` is set — `Equip`
    at every spawn, `ApplyKit` at a locker, and on a client `ApplyAuthoritative` from every
    snapshot — from the catalog's weights against the default kit's (`ReferenceWeightKg`, L1), so
    the default kit is exactly 1. Mounted is still 0 whatever the kit. A jump sets its upward
    velocity whole and is as high in any kit; the jumping and falling states' steering speed is
    scaled, as every movement state's is.
  - **Beyond the plan: the replay takes the kit at once.** `Reconcile` sets the scale before it
    replays, so the first snapshot that carries a new kit replays the inputs the server has not yet
    seen at the speed the server will simulate them at: one correction for a kit change rather than
    a second one a round trip later. §7.1's "the replay uses the current scale" still holds; the
    current scale now includes the record that just arrived.
  - **The record's kit, in one place**: `WeaponCatalog.RecordedKit(weaponFlags, kit)` — the primary
    from the weapon byte, the pouches and the vest from the kit byte — which `ApplyAuthoritative`
    and demo playback both read.
  - **Demos**: journal playback sets each character's `MoveSpeedScale` from its keyframe's kit
    (§6.3); what anyone carried in their arms stays unrecorded, as before.
  - **HUD**: `load 15.3 kg · 88 %` on the status corner's second line, under `HP … grenades …`; the
    corner starts a line higher for it.
  - **The observation**: `self.load_kg` (clamped to 0–40 kg, `AgentObservation.MaxLoadKg`, past
    the heaviest kit's 26.1) and `self.load_scale` (0.70–1.12), extras 6 and 7 after L3's. The
    vector's `move_speed_scale` is `fps_controller.MoveSpeedScale`, now the product, still clamped
    to 1: the default kit reads 1 there as before, and so does any lighter kit, whose factor is
    `self.load_scale`'s to say. The vector and `gdpyr-agent-obs-3` are unchanged.
  - **Beyond the plan: a claim relative to another metric.** §9's claim is a ratio of two seats'
    distances, and the harness could only compare one metric with a constant. An assertion's
    `relative_to` names a second metric; the claim is then about the first divided by the second,
    in the same episode, and a reference that is missing or came to 0 fails (AGENT_API.md §9.1).
    The JSON summary names it.
  - **How distance is measured.** A seat's position is sampled during the warmup too, at the
    roster's spawn, before the scenario's `spawn` puts it in its lane, so the position's minimum or
    maximum would include that teleport. Its velocity there is 0: the episode's sum of a seat's z
    velocity samples, one a tick at `step_mul` 1, is minus its distance times 60 / 16 (the vector divides
    velocity by 16 m/s), and the ratio of two seats' sums is the ratio of their distances.
  - **Scenario `weight_speed.json`**: three lanes 10 m apart at x = −90, −80 and −70, from z = 80
    towards −z, which nothing on the Test map stands in; a seat in each at `step_mul` 1 — stripped
    (no primary and nothing else, 2.0 kg), default (9.3 kg) and heaviest (a launcher, nine pouches
    and a heavy vest, 26.1 kg). Its script, `scripts/walk_then_sprint.json`: 60 ticks still, 4
    walking — the sprinting state is entered from walking on a press of sprint, not from idle — and
    180 sprinting; the episode ends on the sprint's last tick, because a stop's distance goes with
    the square of the speed and would not keep the ratio. Claims, over seeds 6 and 7: each seat's
    `load_kg` and `load_scale`; each reached the sprinting state; top speeds 8.96, 8.0 and 5.6 m/s
    to 1 % (`velocity.2.min`); and the stripped and heaviest seats' summed z velocity relative to the
    default's, 1.12 and 0.70 within 3 %. The movement step puts the distances at 25.9, 23.1 and
    16.2 m (`MovementTests.ApplyMove_DistanceIsLinearInTheScale` pins the ratios).
  - **Who now moves at another speed**: every kit but the default. Every kitted seat in the
    scenarios before this one holds still (each script's `move` is `[0, 0]`). **Bots do not all
    carry the default kit**: since L1 a bot carries its roster slot's primary and the rest of the
    default kit, so a rifle bot moves as before, a DMR bot's 10.6 kg is 0.974 (sprint 7.8 m/s) and a
    launcher bot's 13.7 kg is 0.912 (walk 4.6, sprint 7.3) — a launcher in every third slot
    (`BotDirector.Equip`), and the HTN locker runner for the rest of the life it takes its launcher.
    L-D4's "every scenario's timing stands" holds for the default kit and not for those bots.
    Per-role kits, which would move them again, are L7's.
  - **The scenarios give bots their time in metres.** Kept, not exempted: a bot moves at its kit's
    speed like anyone, and a scenario whose claims wait on ground bots walking is sized for the
    slowest a bot could be — the speed floor, 0.70, below today's slowest bot (0.912) and L7's
    heaviest planned role kit (17.9 kg, 0.828), so neither needs the windows revisited. Its window
    is the one default-speed bots had, divided by 0.70 and rounded up to a whole second
    (AGENT_API.md §9.1). The five scenarios with ground bots whose claims need them to move:
    `htn_ground_denies_node`, `htn_squad_retreats` and `tactics_move_to_node` 5,400 → 7,740 ticks
    (the last one's strategist step with it, 5,340 → 7,680), and `htn_locker_runner_kit` and
    `tactics_sniper_overwatch` 3,600 → 5,160. Each of their claims is a count at or above a floor,
    or the runner's launcher, so a longer window cannot fail one that a shorter one passed;
    `strategist_economy`'s two ground bots are not what it claims about, and every other
    scenario's ground players are seats. That is 17.8 % more ticks across the suite. The bots
    themselves needed nothing for it: at the floor a walk from rest covers 1.25 m in the pilot's
    half-second stuck window, which wants 0.6; its 1.5 m/s sprint threshold is under the slowest
    walk; and the locker trip's 30 s is 105 m at the slowest walk.
  - `dotnet test`: 2332 passed, 0 failed (2324 before; 8 new: 5 in `MovementTests`, 3 in
    `ScenarioTests`; `AgentObservationTests`' extras widened). `dotnet build Gdpyr.csproj`: 0
    warnings, 0 errors; the harness and the .NET client build.
  - **The tests test something.** One change at a time, each failed tests: `load_scale` clamped to
    [0, 1] (1), `load_kg` not written (1), the ratio not taken (1), a reference of 0 divided by (1),
    `relative_to` dropped when the file is read (1), the reference read from another seed's episode
    (1), the movement step's target capped at 8 m/s (2).
  - **Not taken**: every engine-side check. No Godot binary was available in this session: the
    environment's network policy refused the engine's downloads, and its permission policy refused
    building the engine from source. So `weight_speed` has not run over its seeds; the existing
    suite has not run against this build — the five scenarios whose windows were widened for slow
    bots are where it could differ; the default kit's 8.0 m/s sprint has not
    been seen in a round (`weight_speed` claims it); and the debug HUD's mispredictions per second
    on a listen host and over a 100 ms `tc netem` link have not been measured, so §12's "L4
    measures it" is still open. The HUD's load line, demo playback's scale and the replay's have
    compiled and not run.

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
- **Result — built; both resupply scenarios pass engine-side.** This phase is the first in which
  a Godot binary could be downloaded (4.6 stable .NET, checked against its release's SHA-512 sums),
  so the scenarios ran headless, and the whole suite with them; the listen host still has not. One
  event kind, a second scenario file and the armour's granularity differ from the plan; each is
  said below.
  - **The rule** (`Scripts/Sim/LockerSupply.cs`): `KitState` (reserve, grenades, vest points),
    `SupplyPacing` (when each may next be given) and `Step`, §4.4's function with the pacing passed
    beside the kit, since the counts live in the player's weapons, grenades and vest and the pacing
    on the player. A pouch's rounds every 30 ticks, a grenade every 60, each the moment it is short
    and due — so a player who walks up with three empty pouches has the first at once and the third
    a second later — and nothing while `IsLockedOut`, which is `Resupply.CanHeal`'s three seconds,
    the units' rule. Never past the kit (`Ceiling`: the pouches' rounds, the grenade pouches, the
    vest's points), never taking anything away, nothing for a weapon that reloads forever, nothing
    with no primary or no vest. The pacing is kept for the life, so stepping out of the reach and
    back does not buy a pouch sooner; a spawn starts it afresh. A pouch goes to the reserve, not the
    magazine; health is not resupplied.
  - **Armour in handfuls, not per tick.** Thirty points a second is half a point a tick, and points
    are whole; they are given as three every six ticks (`ArmourIntervalTicks`, `ArmourPerGrant`),
    which keeps the rate, fills a medium vest from nothing in 1.9 s and a heavy one in 3.3 s, and
    keeps the event stream below to ten records a second per player rather than thirty.
  - **The pass** (`CombatManager.ServerResupply`), after `ServerRespawns`, as §5.4 says: every living
    ground-force player whose feet are within 1.0 m of a locker's footprint
    (`EmplacementManager.LockerDistance`, L2's) is stepped through `PlayerCombat.TakeResupply`.
    Mounted or carrying makes no difference: L-D7 names neither. Nothing is sent — the record
    carries reserve, grenades and vest points already, the reserve under the owner's
    acknowledgement guard as a reload's and the grenades under the throw's. Counters for the debug
    HUD and the round-over line: players resupplied, rounds, grenades and armour points given this
    round, cleared with the round. A hit on tick 0 is now recorded as tick 1, so that "0 = never"
    cannot drop it.
  - **Beyond the plan: a `resupply` event.** §9's claim "none while a rifleman keeps hitting it" is
    about time, and the harness's metrics are aggregates over an episode: with only `damage`
    records and observations, it could be stated only as "the vest absorbed exactly its points",
    which a gap of three seconds in the rifleman's fire would break for the right reason. So the pass
    emits `resupply` — peer, rounds, grenades, armour, and `since_hurt`, the ticks since anything
    last hurt the player (−1 for never this life) — for each tick it gave anything, and the lockout
    becomes a claim on one field: no record with `since_hurt` from 0 to 179. Kind 22, appended
    (`AgentEventSchema`, the harness's field table, AGENT_API.md §8); JSON-only and additive.
  - **HUD**: the prompt under the reticle gains a second line in a locker's reach while the kit is
    short — `resupplying — 4/5 mags · 2/3 grenades · 40/60 vest`, counting full magazines, for what
    the kit holds — or `resupply waits: under fire` in the lockout. A client's "hit" is its record's
    health or vest points falling, a one-way trip late (`PlayerCombat.LastDamagedTick`, which the
    server never sends); on a listen host it is the server's own. The debug HUD has a `kit` row —
    the living by vest tier and their mean load — and on the authority a `locker` row: kits taken
    and what the resupply gave this round.
  - **The locker** (`WeaponLocker`): its label reads `WEAPON LOCKER / RESUPPLY`, and a green line
    ten centimetres wide lies on the floor along the reach's edge — a metre out all round, square
    along the cabinet's sides and round at its corners — drawn along `LockerSupply.ReachOutline`,
    which `LockerSupplyTests` pins to the footprint distance, so the line is where the reach ends.
    Not on a dedicated server; the navigation bake parses colliders only and never sees it.
  - **Grenades**: NETCODE.md §10.9 says a locker's resupply refills them, and that a kit carries its
    grenade pouches, at most three, rather than "three a life".
  - **Scenarios.** Two files where §9 has one row, because the Test map has one locker and a
    rifleman shooting at one seat shoots at the other. **`locker_resupply.json`**: a rifleman in the
    default kit 3 m from the footprint empties three magazines and throws its three grenades — the
    reserve goes to 90, the grenades to 0, and nothing comes back there — then walks into the reach:
    exactly four `resupply` records, two pouches of 30 and three grenades, the reserve back to 150
    and the grenades to 3 within 3 s of arriving, the magazine still empty, `since_hurt` −1
    throughout. (The plan's "fires 60 rounds at the locker" was dropped: at the locker each reload's
    pouch comes back on the tick it is drawn, so the reserve never reads below 150 and the claim
    would hold with or without a pace.) **`locker_resupply_under_fire.json`**: a medium-vest seat
    crouched 0.5 m from the footprint and an infantry rifleman 32 m out, its micro-autonomy off; the
    seat holds fire for a second, then kills the rifleman. Claims, over seeds 1–8: the rifleman
    killed; 1–7 hits on the seat, and the vest absorbing some; no `resupply` record 0–179 ticks after
    a hit; armour given at least once, the first of it 180 ticks or more after the last hit; the
    armour given equal to what the vest absorbed (`relative_to`, L4's); 60 points at the end; the
    seat alive throughout. Not the plan's "shot down to 0, walks to the locker": a scripted seat
    cannot see when its vest is spent, and the rifleman lands its eighth hit — death, in a medium
    vest — within two seconds of its first: in this file's first draft a seat standing with its fire
    held took its eighth 108 ticks after spawning, and in a probe of a crouched seat that never
    fired (not committed) the first hit came 6–84 ticks after spawning. Sitting at the locker throughout asks
    the same two things — nothing while hit, all of it back after.
  - **From L3: `armour_vs_rifleman.json`'s vest claims read the end of the episode.** Its first
    engine-side run, on the build before this phase, failed two claims: `observation.heavy.self.
    armour_max.min == 100` and `armour_tier.min == 3` read 0 at tick 3 of seed 6, a warmup sample
    taken while the seat was still its bot in the default kit, before the scenario's `spawn` equips
    the heavy vest (the effect L4 found for positions). Every other claim held. They are `.last`
    now; on this phase's build the file passes seeds 6 and 7 — 5 hits kill the bare seat, 10 the
    heavy one, and the heavy vest absorbs 100 — so §4.3's rifle row has been seen in a round.
  - `dotnet test`: 2354 passed, 0 failed (2332 before; 22 new: 20 in `LockerSupplyTests`, 2 in
    `AgentEventTests`). `dotnet build Gdpyr.csproj`: 0 warnings, 0 errors; the harness and the .NET
    client build.
  - **The tests test something.** One change at a time, each failed tests: no lockout (2), a pouch
    every tick (3), grenades at the pouch's pace (1), armour past the vest's points (1), rounds past
    the kit (1), grenades to three whatever the kit (2), an unlimited reserve topped up (1), the ring
    drawn as an ellipse round the cabinet (3). Engine-side, with the lockout removed
    `locker_resupply_under_fire` fails both lockout claims (14 records inside the three seconds in
    seed 1), and with the pass's reach check removed `locker_resupply` fails three claims (the
    reserve never below 150, the grenades never below 3, five records rather than four).
  - **Scenarios** (Godot 4.6 .NET, headless, through the harness, default `htn`):
    `locker_resupply` passed seeds 6 and 7 — in each the walk reached the locker on the same tick,
    and the four records came at it and 30, 60 and 120 ticks later; the first run's hold ended 18
    ticks after the last, and was lengthened by 28; `locker_resupply_under_fire` passed seeds 1–8, 1–4 hits
    on the seat, 10–40 points absorbed, the first armour back exactly 180 ticks after the last hit.
    **The full suite**, twice, one server a file and two runs side by side on separate ports: on the
    build before this phase (`81f0cdd`), 35 of 36 files over 78 seeds, the one failure
    `armour_vs_rifleman`'s two warmup claims above; on this phase's build, 38 of 38 over 88 seeds —
    the 36 and the two new files.
  - **L3's and L4's scenarios, run for the first time** (both runs above). `armour_vs_rifleman` as
    said. `weight_speed` passes seeds 6 and 7: top sprints of 8.96, 8.0 and 5.6 m/s
    (`velocity.2.min` −0.56, −0.5 and −0.35 of 16 m/s), and the stripped and heaviest seats' summed
    z velocity 1.12–1.123 and 0.70 of the default's — so the default kit's sprint is 8.0 m/s as before
    L4. The five scenarios whose windows L4 widened for slow bots (`htn_ground_denies_node`,
    `htn_squad_retreats`, `tactics_move_to_node`, `htn_locker_runner_kit`,
    `tactics_sniper_overwatch`) pass in both runs. L4's misprediction measurements on a listen host
    and over `tc netem` are still not taken, nor is L3's pillbox check.
  - **Not taken**: the listen host, so a person has not seen the paced refill, the HUD's two prompts,
    the debug rows, the label or the ring, and no remote client's view of its own resupply (the
    reserve arriving under the acknowledgement guard) has been watched. The HUD, the debug rows and
    the ring have compiled and run headless, where nothing draws them.

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
- **Result — built; every check in the list taken headless by a probe, none yet by a person.**
  Two things came with the panel that the plan did not name — a latch on keys held through it,
  and the round's end closing the death screen's panel — and the panel stays up on a refusal, as
  §6.2 and §7.2 say and L-D12 does not; each is said below.
  - **The model** (`Scripts/Sim/LoadoutDraft.cs`): `LoadoutDraft`, the kit being edited, over a
    `DraftCatalog` of the weapon catalog's numbers handed in (`WeaponCatalog.DraftCatalogFor`, the
    walk and sprint read off the character's own states, `fps_controller.AuthoredSpeed`). The key
    table (`ActionOf`), what each action does (`Apply`, `ChoosePrimary`, `ChooseVest`), what is
    greyed out (`IsEnabled`: `+` at nine pouches, grenades at three, magazines with no primary,
    `−` at none, the vest at either end, Enter while an answer is on its way, Backspace and `D`
    when they would change nothing), what an answer does (`Answer`), when the panel is no longer
    wanted (`StillWanted` over a `DraftSituation`) and every read-out line, from §4's functions:
    `LOAD 15.3 kg   SPEED 88 %   walk 4.4 · sprint 7.0 m/s`, `VEST 60 pts, absorbs 50 %   rifle hits
    to kill 8 (5 without)`, `AMMO 30 + 150 rifle · 3 grenades` (`AMMO pistol only` with no primary),
    the pouches `▣▣▣▣▣◆◆◆□`. Every kit it holds is one `Loadout.IsValid` accepts, so the panel never
    asks for a kit the validator would refuse as `invalid`. The primaries are in the mock's order,
    keys 1–4: none, rifle, DMR, launcher (`WeaponCatalog.PanelPrimaries`); a request still names them
    by `LargeWeapons`.
  - **The widget** (`Scripts/Ui/LoadoutPanel.cs`, a child of `CombatHud`; `LoadoutMenu.cs` deleted):
    built in code on `Ui/theme.tres` — the radio rows, the steppers, the read-outs and a speed bar
    from 0.70 to 1.12, a status line, and the four keys as buttons at the foot. It draws the draft
    and does what the draft says. Its buttons take no keyboard focus: the keys are the draft's
    table, and a focused button would take Space too.
  - **Holding the player still** (L-D12): `InputFocus.Menu`, beside `TextEntry`; the sampler sends
    neutral frames under either; the pointer is freed on opening and `EnterFirstPerson` recaptures it
    on closing, unless the player has left the ground — the role menu or the strategist's camera has
    it then — or the console is typing, which hands back the pointer it found. The panel takes its
    keys in `_Input` and marks them handled — only keys it has a meaning for, and none while the
    console is open — so Escape never reaches `PauseMenu`, which also ignores Escape while
    `InputFocus.Menu` is set.
  - **Beyond the plan: a key held through the panel is not a press** (`Scripts/Sim/ButtonLatch.cs`).
    The use tracker reads a tap on the key's release (`UseTracker`), and the `E` that closes the
    panel is still down on the first tick the sampler listens again: its release would be a tap,
    and at a locker the tap opens the panel again, every time. The sampler now keeps a button that
    is down when it goes back to listening out of the frames until it is let go — after the pause
    menu and the console too, so a trigger or a key held while either closes is theirs until it is
    let go. The panel's buttons act on release, so a click leaves nothing held. The mask is applied
    where device state becomes a frame, so the server, the owner's prediction and a demo all see the
    frames it leaves.
  - **Opening** (§7.2): `EmplacementManager.ServerUse` now returns what a press meant; its resolution
    is `Resolve`, which `PredictUse` shares — the owning client's reading of its own press from
    `ClientSimulateLocal`, over the same tracker and the same predicted frames, at the point the
    server reads it (after the death check, before the gun). `PlayerCombat.Use` is stepped by the
    owner's process for its own player and by the authority for everyone else's, never by both. On
    a listen host `ServerSimulate` opens the host's own panel from the authority's own resolution.
    The prompt reads `[E] weapon locker`.
  - **Taking** (§7.2): Enter sends `RequestLockerKit`; the answer — `ServerKitResult`, or on the
    authority the validator's on the spot — reaches the draft through `CombatHud.AnswerKit`. A kit
    taken closes the panel. **A refusal is shown and leaves the panel open**, as §6.2 and §7.2 say:
    `refused (too_soon): one kit a quarter-second; take it again`, the kit as asked, Enter enabled
    again. L-D12 lists "a refusal" among what closes the panel; the two detailed sections were
    followed. The console's `locker: kit taken` lines went with the panel.
  - **Closing itself**: `CombatHud.Refresh` asks `StillWanted` every tick — at a locker while the
    player is alive on the ground and within 1.0 m of the footprint (`LockerSupply.InReach`, without
    the validator's slack); from the death screen while they are dead; neither through the role
    menu, a strategist's chair or spectating. **Beyond the plan: the end of the round closes the
    death screen's panel too**, not only the locker's, because the round-over scoreboard is drawn
    above the HUD in the middle of the screen, where the panel is; the death screen's `[E] edit` is
    not offered then. The prompt and the resupply line under the reticle are hidden while the panel
    is up, as the panel covers them.
  - **The death screen** (§7.3): under `down — respawning`, `next spawn: rifle · 5 mags · 3 grenades ·
    no vest · 9.3 kg · 100 %   [E] edit`; the `use` key, read by `CombatHud._UnhandledInput` while the
    player is down, opens the panel on the pending kit, whose Enter sends `ClientSelectLoadout`
    (`RequestLoadout`) and closes and whose Backspace goes back to the next spawn's kit as it was.
    `1`/`2`/`3` while dead are retired with `LoadoutMenu`, and `RequestLoadout(byte)` with them.
  - **The console**: `kit` is removed; `loadout`, the next spawn's kit spelt out, stays.
  - **README**: the locker paragraph rewritten for the panel, and the panel's keys as a table.
  - `dotnet test`: 2427 passed, 0 failed (2354 before; 73 new: 67 in `LoadoutDraftTests`, 6 in
    `ButtonLatchTests`). `dotnet build Gdpyr.csproj`: 0 warnings, 0 errors; the harness builds.
  - **The tests test something.** One change at a time, each failed tests: magazines past nine
    pouches (3), grenades past three (2), Enter while an answer is on its way (1), a kit taken that
    does not close (1), a refusal that closes (6), an answer to nothing taken (1), the reach ignored
    (1), the round's end ignored (2), the death screen's panel kept past the spawn (1), the next
    spawn's Enter leaving the panel open (1), `5` and `6` swapped (2), Backspace always enabled (2),
    an edit keeping the refusal's footer (6), the hammer not forced (1), grenades drawn first (1),
    the vest left out of hits-to-kill (4), a latched button never released (3), a latch that masks
    nothing (4).
  - **Engine-side, by a probe** (Godot 4.6 .NET, headless; a temporary autoload in a copy of the
    project, not committed, that injects key events and mouse clicks into the viewport and the
    actions' state into `Input`, as a device would). **On a listen host**, 63 checks, all passed: at
    0.75 m from the footprint the prompt reads `[E] weapon locker` and a tap of `E` opens the panel on
    the kit carried; W and sprint held for a second with it up move the character 0.000 m; `4`, `6`,
    `7` and `0` edit the kit; clicks choose the DMR, leave a chosen primary chosen and drawn pressed,
    choose a heavy vest and do nothing on a greyed-out `0 +`; Enter takes the kit — the launcher in
    the large slot, a full vest of the tier asked, the kit also the next spawn's — closes the panel,
    and nothing is fired; `E` closes it and letting go of that `E` does not open it again; Escape
    closes it without pausing, and pauses as before once it is closed; with the console open over it,
    `1` and `D` edit nothing, and closing the console leaves the panel up; moved 3 m away it closes
    itself and took nothing; a second kit inside a quarter-second is refused with the footer above
    and the panel up, and taken a moment later; dying with it open closes it and takes nothing; the
    death screen's `E` opens it on the next spawn's kit, Enter keeps an edited kit for the next spawn
    without re-equipping the life just ended, and the respawn carries it; a respawn with the death
    screen's panel open closes it; going up to the strategist's chair closes it. **Against a
    dedicated server with two clients**, 42 checks, all passed: the first client, put at the locker
    by the server, opens the panel from its own prediction of the tap, and every check above up to
    the console's passes there too, Enter closing the panel on `ServerKitResult` and the record
    bringing back the launcher and a full medium vest (the server logged `[locker] peer … took
    launcher · 6 mags · 2 grenades · medium vest`); the second client's record of the first names the
    launcher and the medium vest, and it draws that player's vest band.
  - **Not taken**: anything a person sees. Headless Godot draws nothing, so nobody has looked at the
    panel's layout, its legibility at a real resolution, the death screen's line or the speed bar;
    and the dummy display server reports the pointer as visible whatever is asked of it, so the
    pointer's release on opening and its recapture on closing are untested. The probe's clicks were
    injected at the buttons' own rectangles, which says the buttons work and not that they can be
    found. The probe ran on a 1280 × 720 root window.
  - **Scenarios** (Godot 4.6 .NET, headless, through the harness, default `htn`): the full suite,
    38 of 38 files over 88 seeds, in two runs side by side on separate ports — the same 38 files
    and seeds L5 ran. The game assembly they loaded was built before four changes that no
    dedicated server executes, since it has no local player and builds no panel: a line in
    `LoadoutDraft` that repeated what `Sanitize` does, an unused HUD property, the console's and
    the death screen's kit line moved into `WeaponCatalog.DescribeWithLoad`, and comments. The
    probe above ran on the final build. The SDK was .NET 8.0.132 from Ubuntu's archive, Microsoft's
    download host being refused by this environment's network policy; Godot 4.6 stable .NET was
    downloaded from its GitHub release and checked against the release's SHA-512 sums.

### L7 — Computer players (1½ days)

1. Dry → pistol (§8), both AIs — built in L1, which needed it; L7 sees it working in rounds.
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
- **Result — built; `role` is the default, by the rule above; the default kit's dry share is 6.6 %,
  over the 5 % asked for.** Every `htn_*` scenario passes in both kits, and twenty bot-only rounds
  put the role kits' tickets lost a minute inside L0's spread and beside the default kit's, so the
  game mode's `BotKits` is `role`. What the rule does not weigh is said with the rounds below: the
  role kits run dry twice as often. One thing beyond the plan — a seat is not a role — and where the
  rearm sits in the domain differ from §8's words; each is said below.
  - **Dry → pistol, seen in rounds** (built in L1). In the twenty rounds below, bots hit 137 times
    with the pistol in the default kits and 233 in the role kits, in 22 and 34 of the 50 bot-rounds
    of each; no scenario had seen it before.
  - **The fact** (`Scripts/Sim/Htn/GroundDomain.cs`): `GroundFact.Ammo`, an `AmmoBand` — `Dry` with
    nothing in the magazine, nothing spare and no reload under way (the test the brain puts the
    primary away on), `Low` under one magazine spare, else `Ok`; no primary, and a weapon that
    reloads forever, are `Ok`, having nothing a locker can give them (`AmmoBands.Band`). On the
    scan. Beside it, `MagazineLow` no longer holds for a magazine with nothing spare to fill it from:
    a dry primary still in hand planned `Reload`, which cannot start, until the brain's pistol
    switch changed the weapon the fact is read from.
  - **`Rearm`**: a goal in the `resupply` branch, after `reload`: dry, no contact in sight, not
    suppressed, a locker, and a kit with spare rounds to put back (`GroundContext.CanRearm`;
    `KitHoldsSpares`, on the scan — a kit with no magazine pouches has nothing to rearm). It runs to
    `LockerSupply.StandPoint`, is at the locker 0.8 m from the footprint as the runner is, and stays
    — asking for nothing: the resupply pass fills whoever is in its reach (L5) — until the kit is
    full (`KitFull`, every tick, `LockerSupply.NeedsAnything`) or six seconds after it arrived
    (`RearmStayTicks`); being shoved off its spot does not start the stay again. A bot still dry
    after six seconds — everything locked out — plans the stay again. A contact coming into sight,
    or being suppressed, ends it (its executing conditions); a ghost does not, or with the team
    remembering something most of the time a dry bot would hardly ever go. A walk that takes the
    runner's 30 s is given up on (`FailedRearms`) and no rearm is planned for 30 s
    (`RearmRetryTicks`); that wait is no fact, so the scan that sees it end marks the context dirty,
    or a bot standing off with nothing changing around it would never look again. **Where it sits:**
    §8 put it "after fighting a contact in sight and before advancing"; in the `resupply` branch,
    which comes before `attack`, its conditions put it after every fight in sight and before a
    ghost's investigation, a node and the standoff — and before fighting from cover a contact out of
    sight, which a dry bot leaves for the locker.
  - **The locker runner's kit request** came forward in L2 and is as it was: under the role kits it
    asks for its own kit with a launcher for a primary — a rifleman's, say, a launcher with four
    pouches, two grenades and its light vest — and its next spawn is its role's again.
  - **The debugger's map**: the schema names the fact and its values (`HtnSchema.Ground`), the goal
    is a `Resupply` task (`AiGoals`), the server's map and a spectator's fresh build share a
    signature (`HtnDebugTests`), and the locker is marked while a bot rearms as while it runs.
    The debug HUD's goal counts include `Rearm`.
  - **The role kits** (`Scripts/Sim/BotKits.cs`): `BotKits` `default` or `role`, the game mode's
    `BotKits` (`Match/default_gamemode.tres`), `--bot-kits`; `BotKitNames.KitFor`, by the primary's
    place in `WeaponCatalog.LargeWeapons` — DMR 5 magazines, 1 grenade, no vest (9.8 kg, 0.99);
    rifle 4, 2, light (11.4 kg, 0.958); launcher 3 pouches (6 rounds), 3 grenades, medium (17.9 kg,
    0.828), each a kit the validator takes as it is (`BotKitsTests`). `BotDirector.Kits` is the
    setting under `htn` and `default` under `legacy`, whatever is asked; the server's `[bots]` line
    says which. The `welcome` names it (`bot_kits`), and the harness takes `--bot-kits` and a
    scenario's `bot_kits`, a server naming other kits being a harness error, as for `bot_ai`
    (AGENT_API.md §7.1, §9.1).
  - **Beyond the plan: a seat is not a role.** L2's `reset` gives a seat attached without a kit
    "the kit its bot carried when it was attached", which under the role kits is its slot's role
    kit: the first round below put the idle seat in a light vest. A policy's body would then depend
    on how the server's bots are kitted, against L-D3 (every agent seat carries the default kit) and
    §9's "else the default". Such a seat now takes the default kit with its bot's primary — under the
    default kits the kit it took before — at attach, for its next spawn and every `reset`
    (`AgentServerOps`, AGENT_API.md §2). Scenario **`bot_kits_role_seat_default.json`** (new): with
    the bots in role kits, a seat on the rifle bot's slot ends each episode with 150 rounds spare,
    3 grenades, no vest and a load scale of 1.
  - **The inspect panel** shows any ground player's kit and what is left of it, on a line of its
    own, a person's and a legacy bot's too: `kit rifle · 4 mags · 2 grenades · light vest · rounds
    0 + 0 dry · grenades 1 · vest 12` (`AiValueKey.KitPrimary` to `VestPoints`, nine values
    appended); a planning bot's goal line counts its rearms given up on.
  - **An instrument**: the server's kill line ends `, dry` when the victim's primary had nothing in
    the magazine and nothing spare. Over the rounds below it agreed with the event stream's count
    (`rounds_fired` against the kit's rounds and what the resupply gave) in every death but one,
    a life the event count leaves out because a locker kit changed its primary.
  - `dotnet test`: 2468 passed, 0 failed (2427 before; 41 new: 20 in `GroundHtnTests`, 10 in
    `BotKitsTests`, 6 in `LaunchOptionsTests`, 3 in `AiDebugTextTests`, 1 in `HtnDebugTests`, 1 in
    `ScenarioTests`). `dotnet build Gdpyr.csproj`: 0 warnings, 0 errors; the harness and the .NET
    client build. `GroundHtnTests`' shared-domain traces gained the dry situation, and planning
    every ground bot still allocates nothing on the tick.
  - **The tests test something.** One change at a time, each failed tests: no `Contact≠Visible`
    condition (2), no executing condition for a contact in sight (1), no suppressed condition (1),
    the wait's end not marking the context dirty (1), no wait after a give-up (1), the arrival not
    held (1), a full kit ignored (1), no six-second limit (1), spares not required (1), a reload
    under way read as dry (1), low at one magazine spare (1), the rifle's and the DMR's role kits
    swapped (3), role kits under `default` (3), `bot_kits` not read (1), the kit line for planning
    bots only (2), `dry` not said (1), `--bot-kits` dropped (4).
  - **Scenarios** (Godot 4.6 .NET, headless, through the harness, default `htn`): the seven
    `htn_*` files with `--bot-kits default` and again with `--bot-kits role`, each 7 of 7 over 21
    seeds, on the build before a seat stopped taking its bot's role kit — whose seats hold still far
    from the fight. On the final build, in three runs side by side and a fourth beside
    them: the full suite in the role kits, now the default, 39 of 39 files over 90 seeds — the 38
    before this phase and `bot_kits_role_seat_default` — and the seven `htn_*` files again with
    `--bot-kits default`, 7 of 7 over 21 seeds.
  - **Bot-only rounds**, as L0's: five ground bots and the idle seat holding still at (110, 1, 110)
    against one computer strategist, 21,600 ticks (6 min) after a 60-tick warm-up, seeds 1–10 in
    each kit setting, four servers at a time on separate ports; each round read from its start to its
    first `round_end`, else to the run's end. The scenario files and the script that reads the traces
    are not committed. The rounds ran on the build before the seat's kit was fixed, so in the role
    rounds the idle seat wore its slot's rifle kit and light vest: it died 5 times in the default
    rounds and once in the role ones, of 381 and 382 ground deaths.

    | | Default kits | Role kits |
    |---|---|---|
    | **Tickets lost a minute**: mean · range · sd | **8.04** · 1.83–14.83 · 4.09 | **7.91** · 1.99–14.66 · 4.22 |
    | Ground deaths a round: mean · range | 38.1 · 11–50 | 38.2 · 12–50 |
    | Rounds the ground force lost (out of tickets) · won (strategist eliminated) | 6 · 1 | 5 · 0 |
    | **Bot deaths with the primary dry** | **25 / 376 = 6.6 %** | **53 / 380 = 13.9 %** |
    | … rifle · DMR · launcher | 12/160 · 1/64 · 12/152 | 16/167 · 2/62 · 35/151 |
    | Pistol hits by bots | 137 | 233 |
    | Resupply records giving bots rounds · rounds given | 20 · 180 | 51 · 740 |

  - **The rule.** L0 wrote down its rounds' ground deaths (14–50 under `htn`) and when the 14 that
    ended early ended (213–343 s), not their rate; read from those, its spread was 2.33 tickets a
    minute (14 deaths over the whole 6 min) to 14.08 (50 in 213 s). The role kits' mean, 7.91, is
    inside it, 0.13 from the default kits' 8.04 with a round-to-round deviation of about 4.1 in
    either; each setting has one round below L0's least and one above its most. With every `htn_*`
    scenario passing in both, the role kits are the default (`BotKitNames.Fallback`, the game
    mode's `BotKits`); `--bot-kits default` keeps the kit every bot carried before.
  - **The dry share is over 5 %, in either kit.** In the default kits 6.6 % of bot deaths came with
    the primary dry, against L0's estimate of 4.7 % (rifle), 3.1 % (DMR) and 3.3 % (launcher) at
    five pouches: here 7.5 %, 1.6 % and 7.9 %. L0's figures were the lives that fired more than five
    pouches' worth while reloading forever; these are lives that ran out, with the rearm putting
    some back — 20 resupply records in ten rounds gave the bots 180 rounds. A dry bot with a fight in
    sight fights it with the pistol, as L-D14 asks; what each was doing when it died was not
    recorded. In the role kits 13.9 % ended dry, a launcher's 3 pouches (7 rounds) in 23 % of launcher deaths, and
    the rearm gave 740 rounds. The rule above weighs tickets, and they did not move; a dry launcher
    is one that cannot hurt armour, which the tickets lost a minute do not show. Five or more
    pouches for the launcher, or the default kits, are one constant away (`BotKitNames`).
  - **Two quiet rounds.** Default-kit seed 6 went 12,750 ticks (3.5 min), to the end of its window,
    without a hit, a kill or a contact spotted, and seed 7 8,379 ticks; every other round's longest
    such stretch was 2,669–4,557 ticks. Neither had a resupply record in it, so no bot had reached the
    locker to rearm; what the bots were doing then was not recorded. Both are in the default kits;
    L0 did not look for such stretches, so whether they are new is not known.
  - **Not taken**: anything a person sees — the inspect panel's kit line has been written and read
    back by tests, and not drawn; a listen host and a dedicated server; L0's twenty rounds a setting
    (ten here). Legacy bots were not run: they change only as finite ammunition and the pistol
    switch, both L1's, change them, and L8 re-measures them.

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
